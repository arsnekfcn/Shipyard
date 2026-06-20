using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VRage.Game;

namespace ShipyardPlugin
{
    // Offline mode backend: the shipyard is a LOCAL git repo on disk. Listing/reading/installing are
    // plain file I/O against the working tree (<LocalRepoPath>/<RootFolder>/<cat>/<ship>/...); saving
    // goes through a LibGit2Sharp commit so history is tracked and the repo can be hosted/synced later
    // by the user's own git tooling. Reuses ShipyardApi's scrub/parse/paste/install helpers (same class).
    internal static partial class ShipyardApi
    {
        private static string LocalRoot() => Path.Combine(Auth.LocalRepoPath, Auth.RootFolder);
        private static string LocalShipDir(string categoryShip) =>
            Path.Combine(LocalRoot(), categoryShip.Replace('/', Path.DirectorySeparatorChar));

        private static LibGit2Sharp.Signature LocalSig(string author)
        {
            string a = string.IsNullOrWhiteSpace(author) ? Auth.LocalAuthor : author.Trim();
            return new LibGit2Sharp.Signature(a, a + "@local", DateTimeOffset.Now);
        }

        private static void WriteIfMissing(string path, string content)
        { try { if (!File.Exists(path)) File.WriteAllText(path, content); } catch (Exception ex) { Plugin.Log("WriteIfMissing " + path + ": " + ex.Message); } }

        // Create (or adopt) a local shipyard repo at 'path' and switch the plugin into offline mode.
        public static void InitLocalRepo(string path, string author, string rootFolder, Action onReady)
        {
            path = (path ?? "").Trim();
            if (path.Length == 0) { ShipyardRunner.ShowMessage("Pick a folder for the local shipyard."); return; }
            Auth.SetRootFolder(rootFolder);          // set before the bg work lays out <root>/
            string root = Auth.RootFolder;
            // The root must stay inside the repo: it is opened at 'path' and staged with a repo-relative
            // pathspec, so an absolute or '..'-escaping root would write files the commit never captures.
            string resolvedRoot = Path.GetFullPath(Path.Combine(path, root));
            string repoFull = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, '/') + Path.DirectorySeparatorChar;
            if (Path.IsPathRooted(root) || !resolvedRoot.TrimEnd(Path.DirectorySeparatorChar, '/').StartsWith(repoFull.TrimEnd(Path.DirectorySeparatorChar, '/'), StringComparison.OrdinalIgnoreCase))
            { ShipyardRunner.ShowMessage("The shipyard root folder must be a relative path inside the repo (no leading slash, no '..')."); return; }
            ShipyardRunner.RunWithBusyThen<string>(
                "Creating local shipyard...",
                () =>
                {
                    Directory.CreateDirectory(path);
                    if (!Directory.Exists(Path.Combine(path, ".git"))) LibGit2Sharp.Repository.Init(path);
                    WriteIfMissing(Path.Combine(path, ".gitattributes"), "*.sbc text eol=crlf\n*.png binary\n");
                    WriteIfMissing(Path.Combine(path, ".gitignore"), "bp.sbcB5\n");
                    WriteIfMissing(Path.Combine(path, "README.md"),
                        "# Local shipyard\n\nManaged in-game by the Shipyard plugin (offline mode).\n" +
                        "A normal git repo - add your own remote and push it anywhere if you want it hosted.\n");
                    Directory.CreateDirectory(Path.Combine(path, root));
                    WriteIfMissing(Path.Combine(path, root, ".gitkeep"), "keep\n");
                    using (var repo = new LibGit2Sharp.Repository(path))
                    {
                        LibGit2Sharp.Commands.Stage(repo, "*");
                        try { var sig = LocalSig(author); repo.Commit("shipyard: init local repo", sig, sig, new LibGit2Sharp.CommitOptions()); }
                        catch (LibGit2Sharp.EmptyCommitException) { /* already committed / nothing to add */ }
                    }
                    return path;
                },
                p =>
                {
                    Auth.SetLocalRepo(p, author);
                    Auth.SetMode("offline");
                    ShipyardRunner.ShowResult("Local shipyard ready:\n" + p +
                        "\n\nIt's a normal git repo - add a remote with your own git tools to host it.");
                    onReady?.Invoke();
                });
        }

        // Browse data straight off the local working tree (fast; no network, no boot screen).
        private static ShipyardData LocalData()
        {
            var ships = new List<ShipEntry>();
            try
            {
                // Normalize the root so the prefix-strip can't be thrown off by trailing separators,
                // case, or a long-path/UNC prefix on the enumerated results.
                string root = Path.GetFullPath(LocalRoot()).TrimEnd(Path.DirectorySeparatorChar, '/');
                if (Directory.Exists(root))
                    foreach (var bp in Directory.GetFiles(root, "bp.sbc", SearchOption.AllDirectories))
                    {
                        string shipDir = Path.GetFullPath(Path.GetDirectoryName(bp));
                        string rel = shipDir.Length > root.Length &&
                                     shipDir.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                                     ? shipDir.Substring(root.Length)
                                     : Path.GetFileName(shipDir);   // fallback: just the ship folder name
                        string cs = rel.Replace(Path.DirectorySeparatorChar, '/').Trim('/');
                        if (cs.Length == 0) continue;
                        int sl = cs.LastIndexOf('/');
                        var e = new ShipEntry
                        {
                            CategoryShip = cs,
                            Folder = sl > 0 ? cs.Substring(0, sl) : "",
                            Name = sl >= 0 ? cs.Substring(sl + 1) : cs,
                        };
                        string thumb = Path.Combine(shipDir, "thumb.png");
                        if (File.Exists(thumb)) e.ThumbPath = thumb;
                        string yaml = Path.Combine(shipDir, "ship.yaml");
                        if (File.Exists(yaml)) { try { e.Tags = ParseTags(File.ReadAllText(yaml)); } catch (Exception ex) { Plugin.Log("ParseTags " + yaml + ": " + ex.Message); } }
                        ships.Add(e);
                    }
            }
            catch (Exception ex) { Plugin.Log("LocalData failed: " + ex.Message); }
            ships = ships.OrderBy(s => s.CategoryShip, StringComparer.OrdinalIgnoreCase).ToList();
            Plugin.Log("local browse: " + ships.Count + " ships");
            return new ShipyardData { Ships = ships, Prs = new List<PrEntry>() };
        }

        private static byte[] LocalBlueprintBytes(string categoryShip)
        {
            string bp = Path.Combine(LocalShipDir(categoryShip), "bp.sbc");
            return File.Exists(bp) ? ReadFileShared(bp) : null;
        }

        // "Current version of this ship" bytes: local working tree offline, else main on GitHub.
        // Lets the shared read paths (details, highlight-diff baseline) work in either mode.
        private static byte[] RepoOrLocalBytes(string categoryShip)
            => Auth.IsOffline ? LocalBlueprintBytes(categoryShip) : GetBlueprintBytes(Gh(), "main", categoryShip);

        // Grids ready to spawn/paste from a local ship (owner sentinel flipped like an online install).
        private static MyObjectBuilder_CubeGrid[] LocalGrids(string categoryShip)
        {
            byte[] bytes = LocalBlueprintBytes(categoryShip);
            if (bytes == null) throw new Exception("Local ship not found: " + categoryShip);
            return ParseGrids(PrepareInstalledBp(bytes), categoryShip);
        }

        // Copy a local ship's files into the SE blueprint library (offline 'Install to Library').
        private static int LocalInstall(string categoryShip, out string destName)
        {
            string srcDir = LocalShipDir(categoryShip);
            destName = categoryShip.Substring(categoryShip.LastIndexOf('/') + 1);
            // Don't create an empty blueprint folder in the SE library if there's nothing to install.
            if (!File.Exists(Path.Combine(srcDir, "bp.sbc"))) return 0;
            string dest = Path.Combine(BlueprintsLocal(), destName);
            Directory.CreateDirectory(dest);
            int n = 0;
            foreach (var f in new[] { "bp.sbc", "thumb.png" })
            {
                string sp = Path.Combine(srcDir, f);
                if (!File.Exists(sp)) continue;
                byte[] data = ReadFileShared(sp);
                if (f == "bp.sbc") data = PrepareInstalledBp(data);
                File.WriteAllBytes(Path.Combine(dest, f), data);
                n++;
            }
            return n;
        }

        // Write a ship into the local working tree and commit it. bp must already be scrubbed.
        // thumb==null preserves the existing thumbnail; yamlContent==null preserves the existing
        // ship.yaml (so a grid-only update commit doesn't wipe the thumb/metadata). 'author' is the
        // commit signature (falls back to Auth.LocalAuthor inside LocalSig when null/blank).
        private static void LocalSaveShip(string categoryShip, byte[] scrubbedBp, byte[] thumb, string yamlContent, string commitMsg, string author = null)
        {
            string shipDir = LocalShipDir(categoryShip);
            // Defense in depth: the ship folder must resolve under the shipyard root before we write.
            EnsureUnderLocalRoot(shipDir);
            Directory.CreateDirectory(shipDir);
            File.WriteAllBytes(Path.Combine(shipDir, "bp.sbc"), scrubbedBp);
            if (thumb != null) File.WriteAllBytes(Path.Combine(shipDir, "thumb.png"), thumb);
            if (yamlContent != null) File.WriteAllText(Path.Combine(shipDir, "ship.yaml"), yamlContent);
            using (var repo = new LibGit2Sharp.Repository(Auth.LocalRepoPath))
            {
                // Stage only the affected ship folder (relative to the repo working dir) instead of
                // re-hashing the whole tree on every per-ship save.
                string pathSpec = StagePathSpec(repo, shipDir);
                LibGit2Sharp.Commands.Stage(repo, pathSpec ?? "*");
                var statusOpts = pathSpec != null
                    ? new LibGit2Sharp.StatusOptions { PathSpec = new[] { pathSpec } }
                    : new LibGit2Sharp.StatusOptions();
                if (!repo.RetrieveStatus(statusOpts).IsDirty) return;   // nothing changed
                var sig = LocalSig(author);
                repo.Commit(commitMsg, sig, sig, new LibGit2Sharp.CommitOptions());
            }
        }

        // Throw if 'absPath' resolves outside the local shipyard root (guards against '..' in a
        // user-supplied categoryShip escaping the repo and clobbering/deleting unintended dirs).
        private static void EnsureUnderLocalRoot(string absPath)
        {
            string root = Path.GetFullPath(LocalRoot()).TrimEnd(Path.DirectorySeparatorChar, '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(absPath).TrimEnd(Path.DirectorySeparatorChar, '/') + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Refusing to touch a path outside the local shipyard: " + absPath);
        }

        // The repo-relative pathspec for 'absShipDir' if it lives under the repo working dir, else null
        // (callers fall back to a full-tree "*" stage in that case). Lets per-ship ops avoid a full rescan.
        private static string StagePathSpec(LibGit2Sharp.Repository repo, string absShipDir)
        {
            try
            {
                string workdir = Path.GetFullPath(repo.Info.WorkingDirectory).TrimEnd(Path.DirectorySeparatorChar, '/') + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(absShipDir);
                if (!(full + Path.DirectorySeparatorChar).StartsWith(workdir, StringComparison.OrdinalIgnoreCase)) return null;
                string rel = full.Substring(workdir.Length).Replace(Path.DirectorySeparatorChar, '/').Trim('/');
                return rel.Length == 0 ? null : rel;
            }
            catch { return null; }
        }

        // Offline delete: remove the ship folder from the working tree and commit the deletion to local main.
        public static void LocalDeleteShip(string categoryShip)
        {
            ShipyardRunner.RunWithBusyThen<string>(
                "Deleting " + categoryShip + " locally...",
                () =>
                {
                    string dir = LocalShipDir(categoryShip);
                    // Guard against a categoryShip with '..' resolving outside the shipyard root and
                    // recursively deleting an unintended directory.
                    EnsureUnderLocalRoot(dir);
                    if (!Directory.Exists(dir)) return "Local ship not found: " + categoryShip;
                    Directory.Delete(dir, true);
                    using (var repo = new LibGit2Sharp.Repository(Auth.LocalRepoPath))
                    {
                        // Scope staging/status to the deleted ship folder instead of the whole tree.
                        string pathSpec = StagePathSpec(repo, dir);
                        LibGit2Sharp.Commands.Stage(repo, pathSpec ?? "*");   // stages the deletion too
                        var statusOpts = pathSpec != null
                            ? new LibGit2Sharp.StatusOptions { PathSpec = new[] { pathSpec } }
                            : new LibGit2Sharp.StatusOptions();
                        if (repo.RetrieveStatus(statusOpts).IsDirty)
                        {
                            var sig = LocalSig(Auth.LocalAuthor);
                            repo.Commit("delete: " + categoryShip + " (offline)", sig, sig, new LibGit2Sharp.CommitOptions());
                        }
                    }
                    return "Deleted '" + categoryShip + "' from the local shipyard.";
                },
                msg => { ShipyardRunner.ShowResult(msg); ShipyardScreen.CloseActiveIfOpen(); OpenShipyard(null); });
        }

        // Offline equivalent of Publish: take a local blueprint, scrub it, save+commit to the local repo.
        public static void LocalPublish(string sourceLocal, string category, string slug, string displayName, List<string> tags)
        {
            ShipyardRunner.RunWithBusyThen<string>(
                "Saving '" + displayName + "' to the local shipyard...",
                () =>
                {
                    string srcDir = Path.Combine(BlueprintsLocal(), sourceLocal);
                    string bpPath = Path.Combine(srcDir, "bp.sbc");
                    if (!File.Exists(bpPath)) throw new Exception("Local blueprint not found: " + sourceLocal);

                    bool gps;
                    string xml = ScrubBpFile(ReadFileShared(bpPath), out gps);
                    byte[] thumb = null;
                    string thumbPath = Path.Combine(srcDir, "thumb.png");
                    if (File.Exists(thumbPath)) thumb = ReadFileShared(thumbPath);

                    string cat = SlugPath(category);
                    string slugged = Slug(slug);
                    string cs = (string.IsNullOrEmpty(cat) ? "" : cat + "/") + slugged;
                    bool isUpdate = File.Exists(Path.Combine(LocalShipDir(cs), "bp.sbc"));
                    string author = Auth.LocalAuthor;
                    string stamp = DateTime.Now.ToString("yyyy-MM-dd");

                    // Build a full ship.yaml matching the online schema (name/slug/category/author/
                    // published/tags). On update, preserve the existing identity (name/author/published)
                    // and just refresh the updated/updated-by stamp, like the online path.
                    string yaml = null;
                    string yamlPath = Path.Combine(LocalShipDir(cs), "ship.yaml");
                    if (isUpdate && File.Exists(yamlPath))
                    {
                        try
                        {
                            string existing = File.ReadAllText(yamlPath).Replace("\r\n", "\n");
                            existing = System.Text.RegularExpressions.Regex.Replace(existing, @"(?m)^updated(-by)?:.*\n?", "");
                            if (!existing.EndsWith("\n")) existing += "\n";
                            yaml = existing + "updated: " + stamp + "\nupdated-by: " + author + "\n";
                        }
                        catch (Exception ex) { Plugin.Log("existing local ship.yaml read failed (regenerating): " + ex.Message); }
                    }
                    if (yaml == null)
                        yaml = "name: " + displayName + "\n" + "slug: " + slugged + "\n" + "category: " + cat + "\n" +
                               "author: " + author + "\n" + TagsYaml(tags) + "\n" + "published: " + stamp + "\n" +
                               "description: >-\n  Saved from in-game (offline).\n";

                    LocalSaveShip(cs, System.Text.Encoding.UTF8.GetBytes(xml), thumb, yaml,
                        (isUpdate ? "update: " : "publish: ") + cs + " (offline)", author);
                    string done = (isUpdate ? "Updated '" : "Saved '") + cs + "' in the local shipyard.";
                    if (gps) done += "\n\nGPS coordinates were scrubbed before saving.";
                    return done;
                },
                msg => { ShipyardRunner.ShowResult(msg); ShipyardScreen.CloseActiveIfOpen(); OpenShipyard(null); });
        }
    }
}
