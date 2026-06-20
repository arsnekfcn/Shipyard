using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;

namespace ShipyardPlugin
{
    // Chat slash commands: "/sy <sub> [args]". Hooks MyAPIGateway.Utilities.MessageEntered while a
    // session is live (lifecycle from Plugin.Update/Dispose). Commands are swallowed (not broadcast).
    // Includes a terminal-style folder navigator (cwd + cd/ls/pwd) over the repo's ship tree.
    internal static class ChatCommands
    {
        private static readonly string[] Prefixes = { "/sy", "/shipyard" };

        // Subcommands that only make sense against a GitHub shipyard (rejected in offline mode).
        private static readonly HashSet<string> OnlineOnly =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "checkout", "co", "finish", "release",   // commit/ci work offline (local commit)
                "review", "prs", "approve", "request-changes", "rc", "merge", "reject",
                "vdiff", "diff-pr", "getpr", "install-pr", "pr",
                "access", "collab", "workshop",
            };
        private static bool _hooked;
        private static string _cwd = "";   // current folder under the repo root ("" = root)
        private static string _cwdRepo;    // the repo _cwd belongs to (reset cwd when it changes)

        public static void Tick()
        {
            bool inSession = MyAPIGateway.Session != null && MyAPIGateway.Utilities != null;
            if (inSession && !_hooked)
            {
                MyAPIGateway.Utilities.MessageEntered += OnMessage;
                _hooked = true;
                Plugin.Log("chat commands registered (/sy)");
            }
            else if (!inSession && _hooked)
            {
                // Mirror Unhook(): actually detach the handler. SE/Pulsar can keep Utilities alive across
                // a session teardown, so leaving it attached would double-register OnMessage next session.
                try { if (MyAPIGateway.Utilities != null) MyAPIGateway.Utilities.MessageEntered -= OnMessage; } catch { }
                _hooked = false;
            }
        }

        public static void Unhook()
        {
            if (_hooked && MyAPIGateway.Utilities != null)
                try { MyAPIGateway.Utilities.MessageEntered -= OnMessage; } catch (Exception ex) { Plugin.Log("unhook failed: " + ex.Message); }
            _hooked = false;
        }

        private static void OnMessage(string text, ref bool sendToOthers)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                string t = text.Trim();
                // Split into at most 3. Prefix, subcommand, and "the rest" kept intact so a multi-word
                // arg (ship name / path with spaces) survives. RemoveEmptyEntries collapses extra spaces.
                string[] parts = t.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                string head = parts[0].ToLowerInvariant();   // exact first token (not StartsWith: avoids /system etc.)
                if (Array.IndexOf(Prefixes, head) < 0) return;
                sendToOthers = false;

                // cwd is per-repo: drop a stale path if the target repo changed in Settings.
                string repoNow = Auth.RepoOwner + "/" + Auth.RepoName;
                if (!string.Equals(_cwdRepo, repoNow, StringComparison.OrdinalIgnoreCase)) { _cwdRepo = repoNow; _cwd = ""; }

                string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
                string arg = parts.Length > 2 ? parts[2].Trim() : "";

                // Offline (local git) has no GitHub-side features. PRs, review, collaborators, Workshop,
                // the checkout lock, repo-wide scrub. Reject those with a clear note instead of the raw
                // "Not signed in" error their GitHub calls would throw.
                if (Auth.IsOffline && OnlineOnly.Contains(sub))
                { Echo("'/sy " + sub + "' isn't available offline (it needs a GitHub shipyard)."); return; }

                switch (sub)
                {
                    case "":
                    case "help": Help(); break;
                    case "browse":
                    case "open": ShipyardApi.OpenShipyard(null); break;
                    case "review":
                    case "prs": ShipyardApi.OpenPendingChanges(); break;
                    case "diff": ShipyardApi.DiffLookedAt(arg); break;
                    case "clear": ShipyardApi.ClearHighlights(); break;
                    case "xray": Auth.SetXray(!Auth.XrayHighlights); Echo("Xray highlights " + (Auth.XrayHighlights ? "ON (drawing through blocks)" : "OFF")); break;
                    case "show": SetDiffCat(true, arg); break;
                    case "hide": SetDiffCat(false, arg); break;
                    case "filters":
                    case "view": ShipyardApi.OpenHighlightOptions(); break;
                    case "install":
                    case "get": ShipyardApi.InstallByQuery(Scoped(arg)); break;
                    case "pull":
                        if (arg.Length == 0) ShipyardApi.InstallAll(); else ShipyardApi.InstallByQuery(Scoped(arg));
                        break;
                    case "pull-all":
                    case "pullall":
                    case "sync": ShipyardApi.InstallAll(); break;
                    case "spawn":
                    case "load": ShipyardApi.SpawnByQuery(Scoped(arg)); break;
                    case "checkout":
                    case "co": ShipyardApi.CheckOutByQuery(Scoped(arg)); break;
                    case "commit":
                    case "ci": ShipyardApi.CommitByQuery(Scoped(arg)); break;
                    case "release": ShipyardApi.ReleaseByQuery(Scoped(arg)); break;
                    case "finish": ShipyardApi.FinishByQuery(Scoped(arg)); break;
                    case "project": ShipyardApi.ProjectByQuery(Scoped(arg)); break;
                    // ---- chat parity with the UI ----
                    case "details":
                    case "specs":
                    case "info": ShipyardApi.DetailsByQuery(Scoped(arg)); break;
                    case "delete":
                    case "rm": ShipyardApi.DeleteByQuery(Scoped(arg)); break;
                    case "paste":
                    case "clipboard": ShipyardApi.ClipboardByQuery(Scoped(arg)); break;
                    case "publish": ShipyardApi.PublishByChat(arg); break;
                    case "settings":
                    case "account": ShipyardApi.OpenSettings(); break;
                    case "access":
                    case "collab": ShipyardApi.OpenManageAccess(); break;
                    case "workshop": ShipyardApi.PushToWorkshopByQuery(Scoped(arg)); break;
                    case "approve": if (Num(arg, out var na)) ShipyardApi.Approve(na); else Echo("Usage: /sy approve <PR#>"); break;
                    case "request-changes":
                    case "rc": if (Num(arg, out var nr)) ShipyardApi.RequestChanges(nr); else Echo("Usage: /sy rc <PR#>"); break;
                    case "merge": if (Num(arg, out var nm)) ShipyardApi.MergeByNumber(nm); else Echo("Usage: /sy merge <PR#>"); break;
                    case "reject": if (Num(arg, out var nj)) ShipyardApi.RejectByNumber(nj); else Echo("Usage: /sy reject <PR#>"); break;
                    case "vdiff":
                    case "diff-pr": if (Num(arg, out var nv)) ShipyardApi.VisualDiffByNumber(nv); else Echo("Usage: /sy vdiff <PR#>"); break;
                    case "getpr":
                    case "install-pr": if (Num(arg, out var ni)) ShipyardApi.InstallPrByNumber(ni); else Echo("Usage: /sy getpr <PR#>"); break;
                    case "pr": if (Num(arg, out var np)) ShipyardApi.PrDetailsByNumber(np); else Echo("Usage: /sy pr <PR#>"); break;
                    // ---- terminal navigation ----
                    case "pwd": Echo("/" + _cwd); break;
                    case "cd": Cd(arg); break;
                    case "ls":
                    case "dir": List(arg, true, true); break;
                    case "folders": List(arg, true, false); break;
                    case "ships": List(arg, false, true); break;
                    case "find":
                    case "search": Find(arg); break;
                    default: Echo("Unknown command '" + sub + "'. Try /sy help"); break;
                }
            }
            catch (Exception ex) { Plugin.Log("chat command failed: " + ex); }
        }

        // Show/hide a diff-highlight category by name or color (or "all").
        private static void SetDiffCat(bool shown, string arg)
        {
            string a = (arg ?? "").Trim();
            if (a.Length == 0 || a.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var cat in HighlightManager.Categories) Auth.SetDiffShown(cat, shown);
                Echo((shown ? "Showing" : "Hiding") + " ALL diff highlights.");
                return;
            }
            string c = HighlightManager.CategoryFromAlias(a);
            if (c == null) { Echo("Which? added/removed/changed/recolored/data (or green/red/orange/cyan/magenta), or 'all'."); return; }
            Auth.SetDiffShown(c, shown);
            Echo((shown ? "Showing " : "Hiding ") + c + " highlights.");
        }

        // ---- folder navigation ----
        private static void Cd(string arg)
        {
            string target = ResolvePath(_cwd, arg);
            if (target.Length == 0) { _cwd = ""; Echo("now at /"); return; }
            ShipyardApi.WithShipPaths("cd /" + target, paths =>
            {
                bool exists = paths.Any(p => { string f = FolderOf(p); return f.Equals(target, StringComparison.OrdinalIgnoreCase) || f.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase); });
                if (!exists) { Echo("No such folder: /" + target); return; }
                _cwd = target; Echo("now at /" + _cwd);
            });
        }

        private static void List(string arg, bool showFolders, bool showShips)
        {
            string target = ResolvePath(_cwd, arg);
            ShipyardApi.WithShipPaths("ls /" + target, paths =>
            {
                var subs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var ships = new List<string>();
                string prefix = target.Length == 0 ? "" : target + "/";
                foreach (var cs in paths)
                {
                    string folder = FolderOf(cs), name = NameOf(cs);
                    if (target.Length == 0)
                    {
                        if (folder.Length == 0) ships.Add(name);
                        else subs.Add(folder.Split('/')[0]);
                    }
                    else if (folder.Equals(target, StringComparison.OrdinalIgnoreCase)) ships.Add(name);
                    else if (folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        subs.Add(folder.Substring(prefix.Length).Split('/')[0]);
                }
                Echo("== /" + target + " ==");
                if (showFolders) foreach (var s in subs) Echo("[dir] " + s + "/");
                if (showShips) { ships.Sort(StringComparer.OrdinalIgnoreCase); foreach (var s in ships) Echo("      " + s); }
                if ((!showFolders || subs.Count == 0) && (!showShips || ships.Count == 0)) Echo("(empty)");
            });
        }

        // Search every ship path + tags (anywhere in the tree) for a substring.
        private static void Find(string arg)
        {
            string q = (arg ?? "").Trim();
            if (q.Length == 0) { Echo("Usage: /sy find <text>"); return; }
            ShipyardApi.WithShipInfos("find '" + arg + "'", ships =>
            {
                var hits = ships.Where(s => s.CategoryShip.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                || (s.Tags != null && s.Tags.Any(t => t.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)))
                           .Take(25).ToList();
                Echo("== find '" + arg + "' ==");
                if (hits.Count == 0) Echo("(no matches)");
                else foreach (var h in hits)
                    Echo("  " + h.CategoryShip + (h.Tags != null && h.Tags.Count > 0 ? "   [" + string.Join(", ", h.Tags) + "]" : ""));
            });
        }

        // Resolve arg against cwd: leading "/" = absolute, "." / ".." handled, segments slugged to match repo.
        private static string ResolvePath(string cwd, string arg)
        {
            arg = (arg ?? "").Trim();
            var segs = arg.StartsWith("/") ? new List<string>()
                                           : (cwd.Length == 0 ? new List<string>() : cwd.Split('/').ToList());
            foreach (var raw in arg.Split('/'))
            {
                string s = raw.Trim();
                if (s.Length == 0 || s == ".") continue;
                if (s == "..") { if (segs.Count > 0) segs.RemoveAt(segs.Count - 1); continue; }
                segs.Add(s);
            }
            return ShipyardApi.SlugPath(string.Join("/", segs));
        }

        // A bare name typed while inside a folder is scoped to that folder so resolution is unambiguous.
        private static string Scoped(string arg)
        {
            arg = (arg ?? "").Trim();
            if (arg.Length == 0 || arg.Contains("/") || _cwd.Length == 0) return arg;
            return _cwd + "/" + arg;
        }

        // Parse a PR number argument ("#29" or "29").
        private static bool Num(string a, out int n) => int.TryParse((a ?? "").TrimStart('#', ' ').Trim(), out n);

        private static string FolderOf(string cs) { int l = cs.LastIndexOf('/'); return l > 0 ? cs.Substring(0, l) : ""; }
        private static string NameOf(string cs) { int l = cs.LastIndexOf('/'); return l > 0 ? cs.Substring(l + 1) : cs; }

        private static void Echo(string msg) { try { MyAPIGateway.Utilities.ShowMessage(Brand.Faction, msg); } catch { } }

        private static void Help()
        {
            MyGuiSandbox.AddScreen(new InfoScreen(Brand.Faction + "  //  SHIPYARD COMMAND INTERFACE",
                "Operator console.  Prefix:  /sy   or   /shipyard   (both accepted)\n\n" +
                "NAVIGATE (terminal-style over the repo's ship tree):\n" +
                "/sy ls [path]           -  list folders + ships here (or at path)\n" +
                "/sy folders [path]      -  list just folders\n" +
                "/sy ships [path]        -  list just ships\n" +
                "/sy cd <path>           -  change folder (.. up, / root, e.g. PvP/Frigate)\n" +
                "/sy pwd                 -  show current folder\n" +
                "/sy find <text>         -  search every folder for ships matching text\n" +
                "\n" +
                "ACT (a bare name is scoped to your current folder):\n" +
                "/sy install <ship>      -  pull one ship into your blueprint library\n" +
                "/sy pull [ship]         -  pull one ship, or pull ALL if no name\n" +
                "/sy spawn <ship>        -  spawn a ship into the world\n" +
                "/sy project <ship>      -  load a ship into the projector you're looking at\n" +
                "/sy diff [ship]         -  highlight changes on the ship you're looking at\n" +
                "/sy xray                -  toggle drawing highlights THROUGH blocks\n" +
                "/sy show|hide <what>    -  show/hide a category: added/removed/changed/recolored/data\n" +
                "                           (or green/red/orange/cyan/magenta, or 'all');  /sy filters = UI\n" +
                "/sy clear               -  clear diff highlights\n" +
                "\n" +
                "WORK ON A SHIP (collaborative checkout)  (checkout/finish/release: GitHub shipyards only):\n" +
                "/sy checkout <ship>     -  lock + paste the WIP version to work on\n" +
                "/sy commit [ship]       -  commit the ship you're looking at to its work branch\n" +
                "/sy finish <ship>       -  open the PR that publishes the checkout to main\n" +
                "/sy release <ship>      -  abandon the checkout (delete the work branch)\n" +
                "\n" +
                "MORE ACTIONS:\n" +
                "/sy details <ship>      -  block count / size / owner / tags\n" +
                "/sy paste <ship>        -  load a ship onto your clipboard to place\n" +
                "/sy publish <bp> <folder> [tags]  -  publish a local blueprint as a NEW ship\n" +
                "/sy delete <ship>       -  delete a ship from main (owner only)\n" +
                "/sy workshop <ship>     -  push a shipyard ship to the Steam Workshop  (GitHub shipyards only)\n" +
                "/sy account             -  account / repo settings;   /sy access  -  manage collaborators  (GitHub shipyards only)\n" +
                "\n" +
                "REVIEW PRs (by number)  (GitHub shipyards only):\n" +
                "/sy pr <#>              -  PR details;   /sy vdiff <#>  -  visual diff in world\n" +
                "/sy getpr <#>           -  install the PR's version\n" +
                "/sy approve <#>   /sy rc <#>   /sy merge <#>   /sy reject <#>\n" +
                "\n" +
                "OPEN UI:\n" +
                "/sy browse              -  open the shipyard\n" +
                "/sy review              -  open pending changes (PRs)\n" +
                "/sy help                -  this list"));
        }
    }
}
