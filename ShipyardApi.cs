using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using HarmonyLib;
using Octokit;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ShipyardPlugin
{
    // One ship as shown in the browser. Folders are arbitrary depth under the repo's top folder.
    public class ShipEntry
    {
        public string CategoryShip; // full path under the top folder, e.g. "PvP/Frigate/Missile/Boat"
        public string Folder;       // folder path (everything but the ship), e.g. "PvP/Frigate/Missile" ("" = root)
        public string Name;         // ship slug (last segment), e.g. "Boat"
        public string ThumbPath;    // local cached thumbnail file, or null
        public string Owner;        // CODEOWNERS owner (or null)
        public string CheckedOutBy; // who holds the checkout/ work branch (or null = not checked out)
        public List<string> Tags = new List<string>();   // from ship.yaml (or empty)
    }

    // Combined payload loaded up front when the Shipyard screen opens.
    public class ShipyardData
    {
        public List<ShipEntry> Ships;
        public List<PrEntry> Prs;
    }

    // One repo collaborator (access management).
    public class CollabEntry { public string Login; public string Role; }   // role: read | write | admin

    public class ManageData
    {
        public bool IsAdmin;
        public List<CollabEntry> Collaborators = new List<CollabEntry>();
        public List<string> PendingInvites = new List<string>();
        public string Note;       // set when the token can't manage access (e.g. a least-privilege App)
        public string RepoUrl;    // github.com repo URL, for the "manage on the web" fallback
    }

    // GitHub API layer (Octokit). Reads ships from the repo and installs them into the game.
    internal static partial class ShipyardApi
    {
        // ---- behavioral constants (named so the value and any user-facing text can't drift) ----
        private const int GitHubSettleMs = 1200;     // post-mutation settle before re-fetching (GitHub list lag)
        private const int RetrySleepMs = 120;        // ReadFileShared per-attempt backoff on a transient file lock
        private const double SpawnAheadMeters = 120.0;   // spawn/visual-diff distance ahead of the camera ("~120m")
        private const double DiffRayMeters = 300.0;      // looked-at-grid raycast range
        private const double ProjectorRayMeters = 150.0; // looked-at-projector raycast range
        private const float ClipboardDragPad = 10f;      // extra paste drag length beyond the grid's bounding radius
        private const int RecoverVoteCap = 32;           // RecoverOffset: skip subtypes with more than this many 'to' cells
        private const double DifferentGridRatio = 0.2;   // <20% aligned matches => treat as a different grid

        private static GitHubClient Client(string token) =>
            new GitHubClient(new ProductHeaderValue("Shipyard")) { Credentials = new Credentials(token) };

        // One GitHubClient per session, rebuilt only when the token actually changes. Recreating it (and
        // re-decrypting the token) on every call was pure overhead - the client is thread-safe to reuse.
        private static GitHubClient _gh; private static int _ghRev = -1;
        private static GitHubClient Gh()
        {
            string token = RequireToken();
            if (_gh == null || _ghRev != Auth.Rev) { _gh = Client(token); _ghRev = Auth.Rev; }
            return _gh;
        }

        private static string RequireToken()
        {
            if (!Auth.HasToken) throw new Exception("Not signed in. Open the Shipyard -> Account -> Sign in with GitHub.");
            string token = Auth.Token();
            if (string.IsNullOrEmpty(token)) throw new Exception("Could not read the saved token. Sign in again (Account tab).");
            return token;
        }

        // ---------------------------------------------------------------- account / repo ----
        // In-game GitHub sign-in via OAuth device flow (Octokit). Opens the browser, shows the code,
        // waits for authorization, stores the per-user token. No external tool needed.
        public static void SignIn(Action onComplete)
        {
            var th = new Thread(() =>
            {
                string result; bool ok = false; ShipyardRunner.BoxHandle box = null;
                try
                {
                    var client = new GitHubClient(new ProductHeaderValue("Shipyard"));
                    var req = new OauthDeviceFlowRequest(Auth.ClientId);
                    req.Scopes.Add("repo");
                    var code = client.Oauth.InitiateDeviceFlow(req).GetAwaiter().GetResult();
                    Plugin.Log("device flow: code expires in " + code.ExpiresIn + "s, poll interval " + code.Interval + "s");
                    try { Process.Start(new ProcessStartInfo(code.VerificationUri) { UseShellExecute = true }); }
                    catch (Exception ex) { Plugin.Log("open browser failed: " + ex.Message); }
                    box = ShipyardRunner.ShowSignIn(code.VerificationUri, code.UserCode);
                    var token = client.Oauth.CreateAccessTokenForDeviceFlow(Auth.ClientId, code).GetAwaiter().GetResult();
                    if (token == null || string.IsNullOrEmpty(token.AccessToken)) result = "No access token returned. Try again.";
                    else
                    {
                        var authed = new GitHubClient(new ProductHeaderValue("Shipyard")) { Credentials = new Credentials(token.AccessToken) };
                        var me = authed.User.Current().GetAwaiter().GetResult();
                        Auth.WriteToken(token.AccessToken);
                        Auth.SetLogin(me.Login);
                        ok = true;
                        result = "Signed in as @" + me.Login + ".\n" +
                                 (string.IsNullOrEmpty(Auth.RepoOwner) ? "Now set your repo (Owner + Name) below." : "Repo: " + Auth.RepoOwner + "/" + Auth.RepoName);
                    }
                }
                catch (Exception ex)
                {
                    string m = ex.Message ?? "";
                    if (m.IndexOf("expired", StringComparison.OrdinalIgnoreCase) >= 0)
                        result = "The sign-in code expired. Click 'Sign in with GitHub' to get a new code.";
                    else if (m.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0)
                        result = "Sign-in was denied on GitHub. Click 'Sign in with GitHub' to try again.";
                    else { ShipyardErrors.WipeIfExpired(ex); result = "Sign-in failed:\n" + ShipyardErrors.Explain(ex); }
                    Plugin.Log("SignIn: " + ex);
                }
                // The device-flow poll blocks on a background thread until the user authorizes OR the code
                // expires (minutes later). If the user closed the sign-in panel in the meantime, treat the
                // flow as canceled: don't pop a stale "code expired"/failed dialog over whatever they moved
                // on to. A token that DID arrive is already saved above, so they just show as signed in.
                string r = result; bool success = ok; var boxH = box;
                ShipyardRunner.InvokeOnMain(() =>
                {
                    bool dismissed = boxH?.Screen != null && !boxH.Screen.IsOpened;
                    ShipyardRunner.CloseBox(boxH);
                    if (dismissed && !success) return;
                    ShipyardRunner.ShowMessage(r);
                    if (success) onComplete?.Invoke();
                });
            });
            th.IsBackground = true; th.Name = "ShipyardSignIn"; th.Start();
        }

        // Save the target repo and (if signed in) verify access.
        public static void SaveRepo(string owner, string repo, Action onSaved)
        {
            owner = (owner ?? "").Trim(); repo = (repo ?? "").Trim();
            // GitHub owner/repo names are word chars, dots and hyphens — reject anything else early
            // (also keeps quotes/backslashes out of the hand-written config.json).
            if ((owner.Length > 0 && !Regex.IsMatch(owner, @"^[\w.-]+$")) ||
                (repo.Length > 0 && !Regex.IsMatch(repo, @"^[\w.-]+$")))
            { ShipyardRunner.ShowMessage("Owner / repo name can only contain letters, digits, '.', '_' and '-'."); return; }
            bool changed = !string.Equals(owner, Auth.RepoOwner, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(repo, Auth.RepoName, StringComparison.OrdinalIgnoreCase);
            Auth.SetRepo(owner, repo);
            if (changed)
            {
                // Different shipyard: the session cache and in-memory diff bases all describe the OLD
                // repo - drop them so the next open is a clean cold fetch, not another repo's ships.
                _cache = null; _cacheHead = null;
                _lastCommitBlocks.Clear();
            }
            onSaved?.Invoke();
            if (!Auth.HasToken || string.IsNullOrEmpty(Auth.RepoOwner) || string.IsNullOrEmpty(Auth.RepoName)) return;
            ShipyardRunner.RunWithBusy("Checking access to " + Auth.RepoOwner + "/" + Auth.RepoName + "...", () =>
            {
                try { var r = Gh().Repository.Get(Auth.RepoOwner, Auth.RepoName).GetAwaiter().GetResult();
                    return "Repo set: " + r.FullName + "  -  access OK."; }
                catch (NotFoundException) { return "Saved, but '" + Auth.RepoOwner + "/" + Auth.RepoName + "' was not found or your account can't see it.\nCheck the spelling and that you're a collaborator."; }
                catch (Exception ex) { return "Saved, but the access check failed: " + ex.Message; }
            });
        }

        private static string AppData(params string[] parts)
        {
            var p = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) };
            p.AddRange(parts);
            return Path.Combine(p.ToArray());
        }
        private static string BlueprintsLocal() => AppData("SpaceEngineers", "Blueprints", "local");
        private static string CacheDir() => AppData("Shipyard", "cache");

        // Per-repo top-level folder (user-set, default "Fleet"), as a "<root>/" prefix for all repo paths.
        private static string RootSlash => Auth.RootFolder + "/";

        // Open the tabbed Shipyard screen (Browse / Publish / Review). Fetches ships + open PRs up front.
        // localName = the F10-selected LOCAL blueprint (or null) so the Update tab can offer Add/Update.
        public static void OpenShipyard(string localName) => OpenShipyard(localName, ShipyardScreen.Tab.Browse, null);

        // Session cache: the repo state from the last full fetch + the main HEAD SHA it reflects. A warm
        // open (cache present) shows the menu INSTANTLY from cache and verifies/refreshes in the background
        // Only the FIRST open per session pays the full load + boot sequence.
        private static ShipyardData _cache;
        private static string _cacheHead;

        private static string MainHead(GitHubClient c)
        {
            try { return c.Git.Reference.Get(Auth.RepoOwner, Auth.RepoName, "heads/main").GetAwaiter().GetResult().Object.Sha; }
            catch (Exception ex) { Plugin.Log("MainHead failed: " + ex.Message); return null; }
        }

        // Overload: open straight to a given tab/folder.
        public static void OpenShipyard(string localName, ShipyardScreen.Tab tab, string folder)
        {
            // First boot: let the user pick the Online (GitHub) or Offline (local git) experience.
            if (!Auth.ModeChosen) { MyGuiSandbox.AddScreen(new ModeChooseScreen()); return; }
            if (!Auth.IsConfigured)
            { MyGuiSandbox.AddScreen(Auth.IsOffline ? (MyGuiScreenBase)new OfflineSetupScreen() : new SettingsScreen()); return; }
            ShipyardScreen.CloseActiveIfOpen();   // never stack Shipyard views

            // OFFLINE: browse straight off the local working tree. No network, no boot screen.
            if (Auth.IsOffline)
            {
                var local = LocalData();
                MyGuiSandbox.AddScreen(new ShipyardScreen(local.Ships, local.Prs, localName, tab, folder));
                return;
            }

            if (_cache != null)
            {
                // WARM: open instantly from cache, then verify/refresh silently in the background.
                MyGuiSandbox.AddScreen(new ShipyardScreen(_cache.Ships, _cache.Prs, localName, tab, folder));
                BackgroundResync();
                return;
            }

            // COLD (first open this session): boot sequence plays while the full fetch runs.
            var boot = new BootScreen(localName, tab, folder);
            MyGuiSandbox.AddScreen(boot);
            var th = new Thread(() =>
            {
                ShipyardData data = null; string err = null;
                try { data = FetchAll(); }
                catch (NotFoundException) { err = RepoNotFoundMsg(); }
                catch (Exception ex) { ShipyardErrors.WipeIfExpired(ex); err = ShipyardErrors.Explain(ex); Plugin.Log("open fetch failed: " + ex); }
                var d = data; var e = err;
                ShipyardRunner.InvokeOnMain(() => boot.Finish(d, e));
            });
            th.IsBackground = true; th.Name = "ShipyardBoot"; th.Start();
        }

        // Cheap staleness check on a warm open: one HEAD lookup. If main moved since the cache, refetch
        // and refresh the open menu silently; otherwise the instant cache view was already correct.
        private static void BackgroundResync()
        {
            ShipyardScreen.SetStatus("> syncing");
            var th = new Thread(() =>
            {
                try
                {
                    string head = MainHead(Gh());
                    if (head == null || head != _cacheHead)
                    {
                        // Reuse the HEAD we just resolved so FetchAll doesn't look up heads/main a second time.
                        var data = FetchAll(head);
                        ShipyardRunner.InvokeOnMain(() => ShipyardScreen.ApplyData(data));
                    }
                }
                catch (Exception ex) { Plugin.Log("resync failed: " + ex.Message); }
                ShipyardRunner.InvokeOnMain(() => ShipyardScreen.SetStatus(""));
            });
            th.IsBackground = true; th.Name = "ShipyardResync"; th.Start();
        }

        // Open the account/repo settings screen directly (from the Browse tab's Account button).
        // Offline → the local-shipyard setup screen (which can also switch back to Online).
        public static void OpenSettings() =>
            MyGuiSandbox.AddScreen(Auth.IsOffline ? (MyGuiScreenBase)new OfflineSetupScreen() : new SettingsScreen());

        // ---- "bring your own GitHub App" (least-privilege, zero-trust) ----
        // Just open GitHub's "create a GitHub App" page. The user fills it in themselves following the
        // in-game instructions - deliberately hands-off: the kind of user who wants this route would
        // rather click the buttons than have us pre-fill anything, and the plugin never touches a key.
        public static void OpenCreateAppPage()
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/settings/apps/new") { UseShellExecute = true }); }
            catch (Exception ex)
            {
                Plugin.Log("open create-app page failed: " + ex.Message);
                ShipyardRunner.ShowMessage("Couldn't open the browser. Go here manually:\nhttps://github.com/settings/apps/new");
            }
        }

        // Open a URL in the default browser (web fallbacks, e.g. managing access on github.com).
        public static void OpenUrl(string url)
        {
            try { if (!string.IsNullOrEmpty(url)) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Plugin.Log("open url failed: " + ex.Message); ShipyardRunner.ShowMessage("Couldn't open the browser. URL:\n" + url); }
        }

        private static string RepoNotFoundMsg() =>
            "Repo not found, or your account can't see it:\n    " + Auth.RepoOwner + "/" + Auth.RepoName +
            "\nCheck Owner + Repo name in Account (signed in as @" + Auth.Login + ")," +
            "\nor accept your invite (Account -> Accept repo invitation).";

        // Seed a fresh/empty repo with the structure the shipyard needs (idempotent skips existing).
        public static void InitRepo()
        {
            ShipyardRunner.RunWithBusy("Initializing " + Auth.RepoOwner + "/" + Auth.RepoName + "...",
                () => SeedRepoFiles(Gh(), Auth.RepoOwner, Auth.RepoName, Auth.RootFolder));
        }

        // Seed a repo with the shipyard structure (.gitattributes / .gitignore / CODEOWNERS / <root>/ / README).
        // Idempotent: a file that already exists (422) is skipped; only real failures are reported.
        private static string SeedRepoFiles(GitHubClient client, string owner, string repo, string root)
        {
            var files = new[]
            {
                new[] { ".gitattributes", "*.sbc text eol=crlf\n*.png binary\nbp.sbcB5 binary\n" },
                new[] { ".gitignore", "bp.sbcB5\n" },
                new[] { ".github/CODEOWNERS", "# Shipyard ownership registry. One line per ship:\n# /" + root + "/<category>/<ship>/ @github-handle\n" },
                new[] { root + "/.gitkeep", "keep\n" },
                new[] { "README.md", "# " + repo + "\n\nShip blueprint shipyard, managed in-game by the Shipyard plugin.\n" },
            };
            int created = 0, existed = 0;
            var failed = new List<string>();
            foreach (var f in files)
            {
                try { client.Repository.Content.CreateFile(owner, repo, f[0], new CreateFileRequest("shipyard init: " + f[0], f[1])).GetAwaiter().GetResult(); created++; }
                // 422 ApiValidationException = the file already exists (CreateFile needs a sha to overwrite).
                // Anything else (auth/permission/rate-limit/network) is a REAL failure - surface it.
                catch (ApiValidationException ex) { existed++; Plugin.Log("init " + f[0] + " (exists/skipped): " + ex.Message); }
                catch (Exception ex) { failed.Add(f[0]); Plugin.Log("init " + f[0] + " FAILED: " + ex.Message); }
            }
            string summary = "Repo init: " + created + " file(s) created, " + existed + " already existed/skipped.\n" +
                   "Ships will live under '" + root + "/'.";
            if (failed.Count > 0)
                summary += "\n\nWARNING: " + failed.Count + " file(s) FAILED (not just 'already existed'): " +
                           string.Join(", ", failed) + ".\nThe repo may be partially initialized - check access/permissions and run init again.";
            return summary;
        }

        // QoL: create a brand-new PRIVATE repo on the signed-in user's account, seed it, and make it the
        // active shipyard - so a new user never has to touch GitHub's website. (Needs the broad `repo`
        // scope, i.e. device-flow sign-in; a fine-grained BYO app generally can't create repos.)
        public static void CreateShipyardRepo(string repoName, string topFolder, Action onDone)
        {
            repoName = (repoName ?? "").Trim();
            topFolder = (topFolder ?? "").Trim();
            if (repoName.Length == 0) { ShipyardRunner.ShowMessage("Enter a name for your shipyard repo."); return; }
            if (!Regex.IsMatch(repoName, @"^[\w.-]+$"))
            { ShipyardRunner.ShowMessage("Repo name can only contain letters, digits, '.', '_' and '-'."); return; }
            ShipyardRunner.RunWithBusyThen<string>(
                "Creating your Shipyard repo...",
                () =>
                {
                    var client = Gh();
                    string login = Auth.Login;
                    if (string.IsNullOrEmpty(login)) login = client.User.Current().GetAwaiter().GetResult().Login;
                    // AutoInit creates main + an initial commit so the seed has a base to commit onto. A name
                    // clash surfaces as a 422 (ShipyardErrors.Explain shows GitHub's "name already exists").
                    client.Repository.Create(new NewRepository(repoName)
                    {
                        Private = true,
                        AutoInit = true,
                        Description = "Space Engineers blueprint shipyard - managed in-game by the Shipyard plugin.",
                    }).GetAwaiter().GetResult();
                    Auth.SetRepo(login, repoName);
                    Auth.SetRootFolder(topFolder);
                    _cache = null; _cacheHead = null; _lastCommitBlocks.Clear();   // fresh repo: no stale session cache
                    System.Threading.Thread.Sleep(800);   // let the AutoInit commit/branch settle before seeding
                    string seed = SeedRepoFiles(client, login, repoName, Auth.RootFolder);
                    Plugin.Log("created shipyard repo " + login + "/" + repoName);
                    return "Created private repo  " + login + "/" + repoName + "  and set it as your shipyard.\n" + seed;
                },
                msg => { ShipyardRunner.ShowResult(msg); onDone?.Invoke(); });
        }

        // ----- access management (GitHub collaborators) -----
        private static string RoleOf(Collaborator c)
        {
            if (c.Permissions != null)
            {
                if (c.Permissions.Admin) return "admin";
                if (c.Permissions.Push) return "write";
                return "read";
            }
            return c.RoleName ?? "?";
        }

        public static void OpenManageAccess()
        {
            ShipyardRunner.RunWithBusyThen<ManageData>(
                "Loading access list...",
                () =>
                {
                    var client = Gh();
                    var data = new ManageData();
                    var repo = client.Repository.Get(Auth.RepoOwner, Auth.RepoName).GetAwaiter().GetResult();
                    data.IsAdmin = repo.Permissions != null && repo.Permissions.Admin;
                    data.RepoUrl = repo.HtmlUrl;
                    try
                    {
                        var list = client.Repository.Collaborator.GetAll(Auth.RepoOwner, Auth.RepoName).GetAwaiter().GetResult();
                        foreach (var c in list) data.Collaborators.Add(new CollabEntry { Login = c.Login, Role = RoleOf(c) });
                    }
                    catch (ForbiddenException)
                    {
                        data.Note = "Your sign-in can't manage access from in-game.\n" +
                            "If you use your own GitHub App, add the 'Administration: Read and write' permission\n" +
                            "to it (only you, the owner, can use it) - or just manage collaborators on github.com.";
                    }
                    catch (Exception ex) { Plugin.Log("collab list failed: " + ex.Message); }
                    try
                    {
                        var inv = client.Repository.Invitation.GetAllForRepository(repo.Id).GetAwaiter().GetResult();
                        foreach (var i in inv) data.PendingInvites.Add("@" + i.Invitee.Login + "  (" + i.Permissions + ", pending)");
                    }
                    catch (Exception ex) { Plugin.Log("repo invites failed: " + ex.Message); }
                    return data;
                },
                data => MyGuiSandbox.AddScreen(new ManageAccessScreen(data)));
        }

        // Show the result, then fire onDone. Used so a follow-up (refresh the access screen) only
        // runs AFTER the GitHub op actually completed, not when the background thread was started.
        private static void RunThenDone(string busy, Func<string> work, Action onDone) =>
            ShipyardRunner.RunWithBusyThen(busy, work,
                msg => { ShipyardRunner.ShowMessage(msg); onDone?.Invoke(); });

        // perm = "pull" | "push" | "admin". Add is idempotent: re-adding updates the permission.
        public static void AddCollaborator(string user, string perm, Action onDone)
        {
            user = (user ?? "").Trim().TrimStart('@');
            if (user.Length == 0) { ShipyardRunner.ShowMessage("Enter a GitHub username."); return; }
            RunThenDone("Granting " + perm + " to @" + user + "...", () =>
            {
                var client = Gh();
                client.Repository.Collaborator.Add(Auth.RepoOwner, Auth.RepoName, user, new CollaboratorRequest(perm)).GetAwaiter().GetResult();
                return "Invited/updated @" + user + " (" + perm + ").\n" +
                       "They must ACCEPT the invite: in-game Account -> 'Accept invitation', or on GitHub.";
            }, onDone);
        }

        public static void RemoveCollaborator(string user, Action onDone)
        {
            user = (user ?? "").Trim().TrimStart('@');
            RunThenDone("Removing @" + user + "...", () =>
            {
                var client = Gh();
                client.Repository.Collaborator.Delete(Auth.RepoOwner, Auth.RepoName, user).GetAwaiter().GetResult();
                return "Removed @" + user + " from " + Auth.RepoOwner + "/" + Auth.RepoName + ".";
            }, onDone);
        }

        // Requester side: accept a pending invitation to the configured repo.
        public static void AcceptInvitations(Action onDone)
        {
            RunThenDone("Checking for invitations...", () =>
            {
                var client = Gh();
                var inv = client.Repository.Invitation.GetAllForCurrent().GetAwaiter().GetResult();
                string want = Auth.RepoOwner + "/" + Auth.RepoName;
                int accepted = 0;
                // Only invitations with a usable Repository reference are inspectable; count those so the
                // user-facing total matches what was actually matched (some payloads have a null Repository).
                int usable = 0;
                foreach (var i in inv)
                {
                    if (i.Repository == null) continue;
                    usable++;
                    if (string.Equals(i.Repository.FullName, want, StringComparison.OrdinalIgnoreCase))
                    { client.Repository.Invitation.Accept(i.Id).GetAwaiter().GetResult(); accepted++; }
                }
                if (accepted > 0) return "Accepted " + accepted + " invitation(s). You now have access to " + want + ".";
                return usable > 0
                    ? "You have " + usable + " invitation(s), but none for " + want + "."
                    : "No pending invitations. Ask an admin to add @" + Auth.Login + ".";
            }, onDone);
        }

        private static string Slug(string s)
        {
            s = Regex.Replace(s ?? "", @"[^\w\-. ]", "");
            s = Regex.Replace(s, @"\s+", "-");
            return s.Trim('-', '.', ' ');
        }

        // Slug each '/'-separated segment of a folder path (so "PvP/Gun Frigate" -> "PvP/Gun-Frigate").
        public static string SlugPath(string p) =>
            string.Join("/", (p ?? "").Split('/').Select(Slug).Where(s => s.Length > 0));

        // Chat terminal: fetch the bare ship-path list (fast) and hand it back on the main thread.
        public static void WithShipPaths(string busy, Action<List<string>> onPaths) =>
            ShipyardRunner.RunWithBusyThen<List<string>>(busy, () => FetchShipPaths(), onPaths);

        // Like WithShipPaths but also reads each ship's tags (one ship.yaml fetch per ship) — for tag search.
        public static void WithShipInfos(string busy, Action<List<ShipEntry>> onInfos) =>
            ShipyardRunner.RunWithBusyThen<List<ShipEntry>>(busy, () => FetchShipInfos(), onInfos);

        // Path + tags for every ship (no thumbnails) — used by chat /sy find. NOTE: one ship.yaml blob
        // GET per ship; fine behind a busy spinner, a candidate for batching later.
        public static List<ShipEntry> FetchShipInfos() => Auth.IsOffline ? LocalData().Ships : FetchShipsCore(false);

        // Read + parse the tags from a ship's ship.yaml (via the blob SHA already in the tree). Empty if none.
        private static List<string> TagsFromBlob(GitHubClient client, string sha, string cs)
        {
            try
            {
                var blob = client.Git.Blob.Get(Auth.RepoOwner, Auth.RepoName, sha).GetAwaiter().GetResult();
                return ParseTags(System.Text.Encoding.UTF8.GetString(BlobBytes(blob)));
            }
            catch (Exception ex) { Plugin.Log("tags read failed " + cs + ": " + ex.Message); return new List<string>(); }
        }

        // Parse a flow-style "tags: [a, b, c]" line (the format Publish writes).
        private static List<string> ParseTags(string yaml)
        {
            var tags = new List<string>();
            if (string.IsNullOrEmpty(yaml)) return tags;
            var m = Regex.Match(yaml, @"(?m)^tags:\s*\[(.*?)\]");
            if (m.Success)
                foreach (var part in m.Groups[1].Value.Split(','))
                {
                    string t = part.Trim().Trim('"', '\'');
                    if (t.Length > 0) tags.Add(t);
                }
            return tags;
        }

        // Sanitize + render a tags list as a flow-style YAML list for ship.yaml.
        private static string TagsYaml(List<string> tags)
        {
            var clean = (tags ?? new List<string>())
                .Select(t => (t ?? "").Replace("[", "").Replace("]", "").Replace(",", " ").Replace("\"", "").Trim())
                .Where(t => t.Length > 0)
                .ToList();
            return "tags: [" + string.Join(", ", clean) + "]";
        }

        // Re-fetch ships + open PRs (for an in-place "Refresh" without leaving the screen).
        public static void RefreshData(Action<ShipyardData> onData)
        {
            if (Auth.IsOffline) { onData(LocalData()); return; }   // local re-scan, no network
            ShipyardRunner.RunWithBusyThen<ShipyardData>("Refreshing...", () => FetchAll(), onData);
        }

        private class RepoOpResult { public string Message; public ShipyardData Data; }

        // Run a repo-mutating op inside the busy overlay, then HOLD the overlay through a short settle
        // (GitHub's PR/main list lags right after a merge/close) + a re-fetch, releasing only once the
        // refresh is ready so the open menu is already correct on release.
        // Ops that fail WITHOUT changing the repo should THROW (plain Exception with the operator text):
        // the error box shows immediately and the settle + re-fetch are skipped.
        private static void RunRepoOp(string busy, Func<string> op)
        {
            ShipyardRunner.RunWithBusyThen<RepoOpResult>(
                busy,
                () =>
                {
                    string msg = op();
                    try { Thread.Sleep(GitHubSettleMs); }   // let GitHub settle before re-fetching
                    catch (ThreadInterruptedException) { }   // an interrupt just means proceed to the re-fetch now
                    ShipyardData data = null;
                    try { data = FetchAll(); }
                    catch (Exception ex) { Plugin.Log("post-op refresh failed: " + ex.Message); }
                    return new RepoOpResult { Message = msg, Data = data };
                },
                r =>
                {
                    ShipyardScreen.ApplyData(r.Data);
                    ShipyardRunner.ShowResult(r.Message);
                });
        }

        // Explicit "Add New ship" flow. We STILL try to detect a name collision on main and, if found,
        // warn LOUDLY that this commits a SEPARATE brand-new ship (auto-suffixed slug), never an update.
        public static void AddNewShip(string sourceLocal, string displayName, string category, List<string> tags)
        {
            string cat = SlugPath(category);
            string wantSlug = Slug(displayName);
            if (string.IsNullOrWhiteSpace(cat)) { ShipyardRunner.ShowMessage("Enter a folder (e.g. PvP or PvP/Frigate)."); return; }
            if (string.IsNullOrWhiteSpace(wantSlug)) { ShipyardRunner.ShowMessage("Enter a ship name first."); return; }
            if (string.IsNullOrEmpty(sourceLocal)) { ShipyardRunner.ShowMessage("No local blueprint selected in F10."); return; }

            ShipyardRunner.RunWithBusyThen<string[]>(
                "Checking '" + cat + "' for name collisions...",
                () =>
                {
                    // Collision is only within the SAME target folder (same name elsewhere is fine).
                    HashSet<string> existing;
                    if (Auth.IsOffline)
                    {
                        existing = new HashSet<string>(
                            LocalData().Ships.Where(s => string.Equals(s.Folder, cat, StringComparison.OrdinalIgnoreCase))
                                             .Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
                    }
                    else
                    {
                        var client = Gh();
                        var tree = client.Git.Tree.GetRecursive(Auth.RepoOwner, Auth.RepoName, "main").GetAwaiter().GetResult();
                        string folderPrefix = RootSlash + cat + "/";
                        existing = new HashSet<string>(
                            tree.Tree.Where(x => x.Path.StartsWith(folderPrefix) && x.Path.EndsWith("/bp.sbc"))
                                     .Select(x => x.Path.Substring(folderPrefix.Length).Split('/')[0]), StringComparer.OrdinalIgnoreCase);
                    }
                    bool collided = existing.Contains(wantSlug);
                    string finalSlug = wantSlug;
                    int n = 2;
                    while (existing.Contains(finalSlug)) finalSlug = wantSlug + "-" + n++;
                    return new[] { collided ? "1" : "0", finalSlug };
                },
                res =>
                {
                    bool collided = res[0] == "1";
                    string finalSlug = res[1];
                    if (!collided) { Publish(sourceLocal, cat, finalSlug, displayName, false, tags); return; }

                    ShipyardRunner.Confirm("NAME EXISTS",
                        "'" + wantSlug + "' ALREADY EXISTS in the shipyard.\n" +
                        "This adds a SEPARATE new ship at " + cat + "/" + finalSlug + "\n" +
                        "(it will NOT update the existing one).\n" +
                        "To update instead, cancel and use 'Update' on the Update tab.\n" +
                        "Add as a new, separate ship?",
                        ok => { if (ok) Publish(sourceLocal, cat, finalSlug, displayName, false, tags); });
                });
        }

        // Ownership/privacy scrub applied to everything we push to the repo (publish + WIP commits):
        // zero owners/builders, strip SteamID64s, drop the local install-time DisplayName markers.
        private static string ScrubBp(string xml)
        {
            xml = Regex.Replace(xml, @"<Owner>\d+</Owner>", "<Owner>0</Owner>");
            xml = Regex.Replace(xml, @"<BuiltBy>\d+</BuiltBy>", "<BuiltBy>0</BuiltBy>");
            // Covers the FULL individual SteamID64 range: the prefix is 7656119 for lower account ids and
            // rolls over to 7656120 once accountID passes ~1.02B (76561200000000000) - which is the majority
            // of modern accounts, so 7656119-only silently leaked them. Lookarounds keep it to a standalone
            // 17-digit id (not a digit run embedded in a larger number/decimal). Replaces the id with "0".
            xml = Regex.Replace(xml, @"(?<![\d.\-])7656(119|120)\d{10}(?![\d.])", "0");
            // Workshop item ids are PER-USER publish state (see WorkshopPush), not repo data.
            xml = Regex.Replace(xml, @"<WorkshopId>\d+</WorkshopId>", "<WorkshopId>0</WorkshopId>");
            xml = Regex.Replace(xml, @"<WorkshopIds>.*?</WorkshopIds>", "", RegexOptions.Singleline);
            return xml.Replace("<DisplayName>[SY] ", "<DisplayName>");   // strip the tool's repo-managed marker
        }

        // SE GPS tokens embed a world location: "GPS:<name>:<x>:<y>:<z>:<#color>:". Scripts stash these
        // in CustomData / mod storage, which we deliberately KEEP for loadout diffing - so zero just the
        // three COORDINATES (the name + structure stay, so the script still parses the entry) rather than
        // leak where the ship was. Cheap guard: only run the regex when "GPS:" is actually present.
        private static readonly Regex GpsCoord = new Regex(
            @"(GPS:[^:\r\n]*:)-?\d+(?:\.\d+)?:-?\d+(?:\.\d+)?:-?\d+(?:\.\d+)?:", RegexOptions.Compiled);
        internal static string ScrubGpsText(string s)
            => string.IsNullOrEmpty(s) || s.IndexOf("GPS:", StringComparison.Ordinal) < 0
                ? s : GpsCoord.Replace(s, "${1}0:0:0:");

        // ---- GPS scrub: a captured blueprint embeds WORLD coordinates. Every grid's
        // PositionAndOrientation is literally where it was built (someone's base location), and
        // autopilot/AI blocks carry raw GPS waypoints. Zero all of it before anything is uploaded.
        // Returns true if something was actually scrubbed (lets the repo-wide pass skip clean files).
        private static bool ScrubGrids(MyObjectBuilder_CubeGrid[] grids)
        {
            if (grids == null || grids.Length == 0) return false;
            Vector3D p0 = grids[0].PositionAndOrientation.HasValue
                ? (Vector3D)grids[0].PositionAndOrientation.Value.Position : Vector3D.Zero;
            bool changed = false;
            foreach (var g in grids) changed |= ScrubGrid(g, p0);
            return changed;
        }

        private static bool ScrubGrid(MyObjectBuilder_CubeGrid g, Vector3D p0)
        {
            if (g == null) return false;
            bool changed = false;
            // Rebase: primary grid to the origin, subgrids keep their offsets relative to it. Paste /
            // spawn math only ever uses RELATIVE positions, so nothing changes functionally - the
            // blueprint just stops recording where it was built.
            if (g.PositionAndOrientation.HasValue && p0.LengthSquared() > 0.25)
            {
                var po = g.PositionAndOrientation.Value;
                g.PositionAndOrientation = new MyPositionAndOrientation((Vector3D)po.Position - p0, po.Forward, po.Up);
                changed = true;
            }
            if (g.CubeBlocks == null) return changed;
            foreach (var b in g.CubeBlocks)
            {
                if (b is MyObjectBuilder_RemoteControl rc)
                {
                    if (rc.Waypoints != null || rc.Coords != null) changed = true;
                    rc.Waypoints = null; rc.Coords = null; rc.Names = null;
                    rc.CurrentWaypointIndex = -1; rc.AutoPilotEnabled = false;
                }
                else if (b is MyObjectBuilder_ProjectorBase pj)
                {
                    // a loaded projection is a NESTED blueprint with its own world coordinates
                    if (pj.ProjectedGrid != null) changed |= ScrubGrids(new[] { pj.ProjectedGrid });
                    if (pj.ProjectedGrids != null && pj.ProjectedGrids.Count > 0) changed |= ScrubGrids(pj.ProjectedGrids.ToArray());
                }
                // Automaton (AI block) mission data rides in the block's COMPONENT container,
                // not the block OB itself - autopilot waypoints + the recorded "home" are raw GPS.
                if (b.ComponentContainer != null && b.ComponentContainer.Components != null)
                    foreach (var cd in b.ComponentContainer.Components)
                    {
                        if (cd.Component is MyObjectBuilder_BasicMissionAutopilot ap)
                        {
                            if (ap.Waypoints != null || ap.CurrentWaypoint != null) changed = true;
                            ap.Waypoints = null; ap.CurrentWaypoint = null;
                        }
                        else if (cd.Component is MyObjectBuilder_BasicMissionFollowHome fh)
                        {
                            if (fh.HomeTargetWaypoint != null || fh.NextWanderWaypoint != null) changed = true;
                            fh.HomeTargetWaypoint = null; fh.NextWanderWaypoint = null;
                        }
                        else if (cd.Component is MyObjectBuilder_ModStorageComponent ms && ms.Storage != null && ms.Storage.Dictionary != null)
                        {
                            // CustomData (under MyTerminalBlock's GUID) + any script's own storage:
                            // zero GPS coordinates while keeping the rest of the loadout config intact.
                            foreach (var key in ms.Storage.Dictionary.Keys.ToList())
                            {
                                string v = ms.Storage.Dictionary[key];
                                string scrub = ScrubGpsText(v);
                                if (!string.Equals(scrub, v, StringComparison.Ordinal)) { ms.Storage.Dictionary[key] = scrub; changed = true; }
                            }
                        }
                    }
            }
            return changed;
        }

        // ---- custom data (sorter/script loadouts) ----
        // CustomData lives in the block's MOD-STORAGE component under this well-known GUID
        // (MyTerminalBlock.m_storageGuid). Inventory-sorter loadouts, script configs etc. are all
        // stored there, so diffing it = diffing the loadout.
        private static readonly Guid CustomDataGuid = new Guid("74DE02B3-27F9-4960-B1C4-27351F2B06D1");

        // The block's CustomData text from a blueprint OB (null if none).
        internal static string ObCustomData(MyObjectBuilder_CubeBlock b)
        {
            try
            {
                var cc = b.ComponentContainer;
                if (cc == null || cc.Components == null) return null;
                foreach (var cd in cc.Components)
                {
                    var ms = cd.Component as MyObjectBuilder_ModStorageComponent;
                    if (ms != null && ms.Storage != null && ms.Storage.Dictionary != null)
                    {
                        string v;
                        if (ms.Storage.Dictionary.TryGetValue(CustomDataGuid, out v)) return v;
                    }
                }
            }
            catch { }   // best-effort read of a block's mod-storage entry; absent/unreadable -> null (caller handles)
            return null;
        }

        private static string DataSig(MyObjectBuilder_CubeBlock b) => NormalizeCd(ScrubGpsText(ObCustomData(b) ?? ""));

        // Normalize CustomData so a blueprint round-trip can't read as a change on its own: unify line
        // endings, strip per-line trailing whitespace, and drop trailing blank lines. (Real content edits
        // still differ; only cosmetic CRLF/whitespace drift is neutralized.)
        private static string NormalizeCd(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = s.Split('\n');
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd();
            return string.Join("\n", lines).TrimEnd('\n');
        }

        // Two blocks at the same position+subtype whose CustomData differs = a data change.
        private static bool SettingsDiffer(MyObjectBuilder_CubeBlock oldB, MyObjectBuilder_CubeBlock newB)
            => !string.Equals(DataSig(oldB), DataSig(newB), StringComparison.Ordinal);

        // The player-given name of a terminal block (e.g. "Cargo: Ammo"), null for armor etc.
        private static string ObBlockName(MyObjectBuilder_CubeBlock b)
        {
            var t = b as MyObjectBuilder_TerminalBlock;
            return t != null && !string.IsNullOrEmpty(t.CustomName) ? t.CustomName : null;
        }

        // Human-readable side of the data diff (the Ctrl+Shift+D window): the block's GPS-scrubbed
        // CustomData, line-diffed by TextDiffScreen.
        // Shares DataSig's exact normalization so the displayed data can never drift from the diff signature.
        private static string SettingsDisplay(MyObjectBuilder_CubeBlock b) => DataSig(b);

        // Full scrub of bp.sbc bytes: structural GPS scrub (positions/waypoints/workshop ids) +
        // the string-level ownership scrub. Falls back to string-only if the XML doesn't parse,
        // so a scrub can never block an upload.
        private static string ScrubBpFile(byte[] bytes, out bool gpsChanged)
        {
            gpsChanged = false;
            try
            {
                MyObjectBuilder_Definitions defs; ulong sz;
                if (MyObjectBuilderSerializer.DeserializeXML(bytes, out defs, out sz) && defs.ShipBlueprints != null)
                {
                    foreach (var bp in defs.ShipBlueprints)
                    {
                        gpsChanged |= ScrubGrids(bp.CubeGrids);
                        if (bp.WorkshopId != 0 || bp.WorkshopIds != null) { bp.WorkshopId = 0; bp.WorkshopIds = null; gpsChanged = true; }
                    }
                    using (var ms = new MemoryStream())
                        if (MyObjectBuilderSerializerKeen.SerializeXML(ms, defs))
                            return ScrubBp(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
                }
            }
            catch (Exception ex) { Plugin.Log("ScrubBpFile structural scrub failed: " + ex.Message); }
            return ScrubBp(System.Text.Encoding.UTF8.GetString(bytes));
        }

        // Read a file even while another process holds it open. SE keeps local blueprint files
        // (Blueprints\local\...\bp.sbc / thumb.png) open - a plain File.ReadAllBytes can then throw
        // "the process cannot access the file ... because it is being used by another process".
        // FileShare.ReadWrite tolerates a concurrent reader/writer; a short retry covers a mid-save lock.
        // Callers run on a background thread, so the brief sleep is safe.
        internal static byte[] ReadFileShared(string path)
        {
            // Explicit bound: attempts 0..3 (1 try + 3 retries). The loop exits by returning on success;
            // on the 4th IOException the when-filter is false, so the exception propagates out of the loop.
            for (int attempt = 0; attempt <= 3; attempt++)
            {
                try
                {
                    using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                    {
                        var buf = new byte[fs.Length];
                        int off = 0, n;
                        while (off < buf.Length && (n = fs.Read(buf, off, buf.Length - off)) > 0) off += n;
                        return buf;
                    }
                }
                catch (IOException) when (attempt < 3) { System.Threading.Thread.Sleep(RetrySleepMs); }
            }
            // Unreachable: attempt==3 rethrows via the filter above, but the compiler needs a terminal path.
            throw new IOException("Could not read " + path + " after retries.");
        }

        // Drop any CODEOWNERS line assigning the EXACT path 'rel' (e.g. "Fleet/PvP/Boat"), tolerant of the
        // space/tab between "/rel/" and "@owner" - matching how ReadCodeowners parses owner lines. Shared by
        // Publish (re-record) and DeleteShip (remove) so all three agree on the format and exact-path anchor.
        private static List<string> CodeownersWithout(string co, string rel)
        {
            var rx = new Regex(@"^\s*/" + Regex.Escape(rel) + @"/\s+@");
            return (co ?? "").Replace("\r\n", "\n").Split('\n').Where(l => !rx.IsMatch(l)).ToList();
        }

        // Publish a LOCAL blueprint to the repo via the GitHub API (no git/PowerShell):
        // scrub ownership -> create blobs/tree/commit -> branch ship/<cat>/<slug> -> PR.
        // Explicit publish: read files from the local blueprint 'sourceLocal', write to the repo at
        // <root>/<category>/<slug>. 'displayName' is the ship.yaml name. isUpdate skips re-uploading the thumb.
        public static void Publish(string sourceLocal, string category, string slug, string displayName, bool isUpdate, List<string> tags)
        {
            // Offline: no PR - write + commit to the local repo instead.
            if (Auth.IsOffline) { LocalPublish(sourceLocal, category, slug, displayName, tags); return; }
            RunRepoOp("OPENING PR - publishing '" + displayName + "'...", () =>
            {
                string srcDir = Path.Combine(BlueprintsLocal(), sourceLocal);
                string bpPath = Path.Combine(srcDir, "bp.sbc");
                if (!File.Exists(bpPath)) throw new Exception("Local blueprint not found: " + sourceLocal);

                bool gpsScrubbed;
                string xml = ScrubBpFile(ReadFileShared(bpPath), out gpsScrubbed);

                // On an update the tree inherits main's thumbnail (BaseTree), so skip re-uploading it.
                byte[] thumb = null;
                string thumbPath = Path.Combine(srcDir, "thumb.png");
                if (!isUpdate && File.Exists(thumbPath)) thumb = ReadFileShared(thumbPath);

                var client = Gh();
                string owner = Auth.RepoOwner, repo = Auth.RepoName;
                slug = Slug(slug);
                string cat = SlugPath(category);
                string rel = RootSlash + cat + "/" + slug;
                string branch = "ship/" + cat + "/" + slug;
                string stamp = DateTime.Now.ToString("yyyy-MM-dd");

                // Fire the independent calls concurrently. The multi-MB bp.sbc upload dominates, so the
                // user/ref/CODEOWNERS reads + the small blob uploads overlap with it instead of serially.
                var bpTask = client.Git.Blob.Create(owner, repo, new NewBlob { Content = xml, Encoding = EncodingType.Utf8 });
                var userTask = client.User.Current();
                var refTask = client.Git.Reference.Get(owner, repo, "heads/main");
                var coGetTask = client.Repository.Content.GetAllContents(owner, repo, ".github/CODEOWNERS");
                var thumbTask = thumb != null
                    ? client.Git.Blob.Create(owner, repo, new NewBlob { Content = Convert.ToBase64String(thumb), Encoding = EncodingType.Base64 })
                    : null;
                // On update, fetch the existing ship.yaml so its identity (name/author/published) survives.
                var yamlGetTask = isUpdate
                    ? client.Repository.Content.GetAllContents(owner, repo, rel + "/ship.yaml")
                    : null;

                string author = userTask.GetAwaiter().GetResult().Login;
                Reference mainRef;
                try { mainRef = refTask.GetAwaiter().GetResult(); }
                catch (NotFoundException)
                {
                    throw new Exception("This repo has no 'main' branch yet.\n" +
                        "Open Account -> Initialize repo (or push an initial commit to 'main'), then publish again.");
                }
                string parentSha = mainRef.Object.Sha;
                var baseCommit = client.Git.Commit.Get(owner, repo, parentSha).GetAwaiter().GetResult();

                // Update: keep the existing ship.yaml (original name/author/published intact) and just
                // refresh the updated/updated-by stamp. New ship (or no yaml found): generate fresh.
                string yaml = null;
                if (yamlGetTask != null)
                {
                    try
                    {
                        var existing = yamlGetTask.GetAwaiter().GetResult();
                        if (existing.Count > 0 && !string.IsNullOrEmpty(existing[0].Content))
                        {
                            yaml = existing[0].Content.Replace("\r\n", "\n");
                            yaml = Regex.Replace(yaml, @"(?m)^updated(-by)?:.*\n?", "");
                            if (!yaml.EndsWith("\n")) yaml += "\n";
                            yaml += "updated: " + stamp + "\nupdated-by: " + author + "\n";
                        }
                    }
                    catch (Exception ex) { Plugin.Log("existing ship.yaml read failed (regenerating): " + ex.Message); }
                }
                if (yaml == null)
                    yaml = "name: " + displayName + "\n" + "slug: " + slug + "\n" + "category: " + cat + "\n" +
                           "author: " + author + "\n" + TagsYaml(tags) + "\n" + "published: " + stamp + "\n" +
                           "description: >-\n  Published from in-game.\n";
                var yamlBlob = client.Git.Blob.Create(owner, repo, new NewBlob { Content = yaml, Encoding = EncodingType.Utf8 }).GetAwaiter().GetResult();

                var newTree = new NewTree { BaseTree = baseCommit.Tree.Sha };
                newTree.Tree.Add(new NewTreeItem { Path = rel + "/ship.yaml", Mode = "100644", Type = TreeType.Blob, Sha = yamlBlob.Sha });

                // CODEOWNERS: record this ship's owner (read fired in parallel above). Only written
                // when the ship is NEW or the publisher already owns it. Otherwise an update PR from
                // a non-owner would silently transfer ownership to the updater on merge.
                try
                {
                    string co = "";
                    try { var existing = coGetTask.GetAwaiter().GetResult(); if (existing.Count > 0) co = existing[0].Content ?? ""; }
                    catch (NotFoundException) { /* no CODEOWNERS yet */ }
                    var ownerMatch = Regex.Match(co, @"(?m)^\s*/" + Regex.Escape(rel) + @"/\s+@(\S+)");
                    string recordedOwner = ownerMatch.Success ? ownerMatch.Groups[1].Value : null;
                    if (recordedOwner != null && !string.Equals(recordedOwner, author, StringComparison.OrdinalIgnoreCase))
                        Plugin.Log("CODEOWNERS untouched: " + rel + " is owned by @" + recordedOwner);
                    else
                    {
                        var lines = CodeownersWithout(co, rel);
                        lines.Add("/" + rel + "/ @" + author);
                        string newCo = string.Join("\n", lines).TrimEnd('\n') + "\n";
                        var coBlob = client.Git.Blob.Create(owner, repo, new NewBlob { Content = newCo, Encoding = EncodingType.Utf8 }).GetAwaiter().GetResult();
                        newTree.Tree.Add(new NewTreeItem { Path = ".github/CODEOWNERS", Mode = "100644", Type = TreeType.Blob, Sha = coBlob.Sha });
                    }
                }
                catch (Exception ex) { Plugin.Log("CODEOWNERS update skipped: " + ex.Message); }

                // Now wait on the big uploads (they ran while we did everything above).
                newTree.Tree.Add(new NewTreeItem { Path = rel + "/bp.sbc", Mode = "100644", Type = TreeType.Blob, Sha = bpTask.GetAwaiter().GetResult().Sha });
                if (thumbTask != null)
                    newTree.Tree.Add(new NewTreeItem { Path = rel + "/thumb.png", Mode = "100644", Type = TreeType.Blob, Sha = thumbTask.GetAwaiter().GetResult().Sha });

                var treeResp = client.Git.Tree.Create(owner, repo, newTree).GetAwaiter().GetResult();

                // commit + branch (create or force-update)
                var commit = client.Git.Commit.Create(owner, repo,
                    new NewCommit(slug + ": publish from in-game", treeResp.Sha, parentSha)).GetAwaiter().GetResult();

                bool branchExists = true;
                try { client.Git.Reference.Get(owner, repo, "heads/" + branch).GetAwaiter().GetResult(); }
                catch (NotFoundException) { branchExists = false; }
                if (branchExists)
                    client.Git.Reference.Update(owner, repo, "heads/" + branch, new ReferenceUpdate(commit.Sha, true)).GetAwaiter().GetResult();
                else
                    client.Git.Reference.Create(owner, repo, new NewReference("refs/heads/" + branch, commit.Sha)).GetAwaiter().GetResult();

                // PR: reuse the open one for this branch if it exists, else create.
                string prInfo;
                var openPrs = client.PullRequest.GetAllForRepository(owner, repo,
                    new PullRequestRequest { State = ItemStateFilter.Open, Head = owner + ":" + branch }).GetAwaiter().GetResult();
                if (openPrs.Count > 0)
                    prInfo = "PR #" + openPrs[0].Number + " updated";
                else
                {
                    var pr = client.PullRequest.Create(owner, repo, new NewPullRequest(slug + ": publish", branch, "main")).GetAwaiter().GetResult();
                    prInfo = "PR #" + pr.Number + " opened";
                }

                Plugin.Log("published " + rel + " on " + branch + " by " + author + " (" + prInfo + ")");
                return "Published '" + displayName + "' to " + cat + "/" + slug + " (" + prInfo + ").";
            });
        }

        // Every <root>/.../<ship>/bp.sbc on main, with owner + tags; withThumbs also ensures a
        // locally-cached thumbnail per ship for the tiles.
        // One open = one CODEOWNERS read shared by both fetches. Every full
        // fetch refreshes the session cache + the HEAD it reflects, so warm opens stay correct.
        private static ShipyardData FetchAll() => FetchAll(null);

        // knownHead: main's HEAD SHA if the caller already resolved it (e.g. BackgroundResync's staleness
        // check), so a full fetch doesn't issue a SECOND heads/main lookup for the same value. Null = resolve.
        private static ShipyardData FetchAll(string knownHead)
        {
            var client = Gh();
            string head = knownHead ?? MainHead(client);
            var owners = ReadCodeowners(client);
            var data = new ShipyardData { Ships = FetchShips(owners), Prs = FetchPullRequests(owners) };
            _cache = data;
            _cacheHead = head;
            return data;
        }

        private static List<ShipEntry> FetchShips(Dictionary<string, string> owners = null) => FetchShipsCore(true, owners);

        private static List<ShipEntry> FetchShipsCore(bool withThumbs, Dictionary<string, string> owners = null)
        {
            var client = Gh();
            var tree = client.Git.Tree.GetRecursive(Auth.RepoOwner, Auth.RepoName, "main").GetAwaiter().GetResult();
            var blobs = tree.Tree.Where(x => x.Type == TreeType.Blob).ToList();
            if (owners == null) owners = ReadCodeowners(client);
            var checkouts = FetchCheckouts(client);

            var entries = new List<ShipEntry>();
            foreach (var b in blobs.Where(x => x.Path.StartsWith(RootSlash) && x.Path.EndsWith("/bp.sbc")))
            {
                string cs = b.Path.Substring(RootSlash.Length, b.Path.Length - RootSlash.Length - 7); // "PvP/Frigate/Boat"
                int lastSlash = cs.LastIndexOf('/');
                entries.Add(new ShipEntry
                {
                    CategoryShip = cs,
                    Folder = lastSlash > 0 ? cs.Substring(0, lastSlash) : "",
                    Name = lastSlash > 0 ? cs.Substring(lastSlash + 1) : cs,
                    Owner = owners.TryGetValue(RootSlash + cs, out var o) ? o : null,
                    CheckedOutBy = checkouts.TryGetValue(cs, out var co) ? co : null
                });
            }
            // Tags = one ship.yaml blob GET each; fire them CONCURRENTLY instead of N sequential round-trips.
            ParallelForEach(entries, e =>
            {
                var y = blobs.FirstOrDefault(x => x.Path == RootSlash + e.CategoryShip + "/ship.yaml");
                if (y != null) e.Tags = TagsFromBlob(client, y.Sha, e.CategoryShip);
                if (withThumbs)
                {
                    var thumb = blobs.FirstOrDefault(x => x.Path == RootSlash + e.CategoryShip + "/thumb.png");
                    if (thumb != null) e.ThumbPath = CachedThumb(client, e.CategoryShip, thumb.Sha);
                }
            });
            Plugin.Log("fetched " + entries.Count + " ships" + (withThumbs ? "" : " (no thumbs)"));
            return entries.OrderBy(x => x.CategoryShip, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Run 'body' over every item with bounded concurrency (GitHub is fine with a handful of parallel
        // requests; the shared GitHubClient is thread-safe). Per-item bodies guard their own exceptions.
        private static void ParallelForEach<T>(IList<T> items, Action<T> body)
        {
            if (items.Count == 0) return;
            if (items.Count == 1) { body(items[0]); return; }
            try { System.Threading.Tasks.Parallel.ForEach(items,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 }, body); }
            catch (AggregateException) { /* per-item bodies already log/guard; don't fail the whole fetch */ }
        }

        // SHA-keyed thumbnail cache: the blob is only downloaded when the thumb actually changed
        // (the repo SHA differs from the one recorded beside the cached PNG).
        private static string CachedThumb(GitHubClient client, string cs, string sha)
        {
            string dir = Path.Combine(CacheDir(), cs.Replace('/', '_'));
            // thumb.sha2: cache marker bumped from thumb.sha so pre-normalization caches re-download once.
            string tp = Path.Combine(dir, "thumb.png"), sp = Path.Combine(dir, "thumb.sha2");
            try
            {
                if (File.Exists(tp) && File.Exists(sp) && File.ReadAllText(sp).Trim() == sha) return tp;
                byte[] data = NormalizeThumb(BlobBytes(client.Git.Blob.Get(Auth.RepoOwner, Auth.RepoName, sha).GetAwaiter().GetResult()));
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(tp, data);
                File.WriteAllText(sp, sha);
                return tp;
            }
            catch (Exception ex)
            {
                Plugin.Log("thumb cache failed " + cs + ": " + ex.Message);
                return File.Exists(tp) ? tp : null;
            }
        }

        // Tile thumbnails come from the in-game F10 screenshot, whose pixel aspect varies with the
        // saver's UI/resolution (most are 682x383 ~16:9, but some are 788x332 ~21:9). The tile grid
        // sizes cells for a single aspect, so an off-aspect thumb renders short and leaves a dead band
        // ("cut off"). Pad every cached thumb to a uniform 16:9 (transparent letterbox/pillarbox,
        // invisible on the dark tile) so they all fill their cell.
        private static readonly double ThumbAspect = 16.0 / 9.0;
        private static byte[] NormalizeThumb(byte[] png)
        {
            if (png == null || png.Length == 0) return png;
            try
            {
                using (var ms = new MemoryStream(png))
                using (var src = new System.Drawing.Bitmap(ms))
                {
                    int sw = src.Width, sh = src.Height;
                    if (sw <= 0 || sh <= 0) return png;
                    double a = (double)sw / sh;
                    int tw = sw, th = sh;
                    if (a > ThumbAspect + 0.01) th = (int)Math.Round(sw / ThumbAspect);        // too wide -> pad top/bottom
                    else if (a < ThumbAspect - 0.01) tw = (int)Math.Round(sh * ThumbAspect);   // too narrow -> pad sides
                    else return png;                                                            // already 16:9
                    using (var dst = new System.Drawing.Bitmap(tw, th, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(dst))
                        {
                            g.Clear(System.Drawing.Color.Transparent);
                            g.DrawImage(src, (tw - sw) / 2, (th - sh) / 2, sw, sh);
                        }
                        using (var outMs = new MemoryStream())
                        {
                            dst.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
                            return outMs.ToArray();
                        }
                    }
                }
            }
            catch (Exception ex) { Plugin.Log("NormalizeThumb failed: " + ex.Message); return png; }
        }

        // A local blueprint plus its thumbnail path (for the Publish grid). ThumbPath is null if absent.
        public class LocalBp { public string Name; public string ThumbPath; }

        public static List<LocalBp> LocalBlueprintsDetailed()
        {
            var list = new List<LocalBp>();
            try
            {
                string root = BlueprintsLocal();
                if (Directory.Exists(root))
                    foreach (var d in Directory.GetDirectories(root))
                    {
                        if (!File.Exists(Path.Combine(d, "bp.sbc"))) continue;
                        string tp = Path.Combine(d, "thumb.png");
                        list.Add(new LocalBp { Name = Path.GetFileName(d), ThumbPath = File.Exists(tp) ? tp : null });
                    }
            }
            catch (Exception ex) { Plugin.Log("LocalBlueprintsDetailed failed: " + ex.Message); }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        // Rewrites applied to the local copy on install:
        // (1) "[SY]" DisplayName marker: shows in the F10 detail pane which BPs are repo-managed.
        // (2) Flip the scrubbed <Owner>0 to a non-zero sentinel: the native blueprint-paste only
        //     reassigns NON-zero owners to the pasting player (MyGuiBlueprintScreen_Reworked.
        //     CopyBlueprintPrefabToClipboard), so a 0-owner ship would paste unowned forever.
        // Publish scrubs both back, so install -> publish round-trips stay clean.
        private static byte[] PrepareInstalledBp(byte[] bytes)
        {
            try
            {
                string xml = System.Text.Encoding.UTF8.GetString(bytes);
                var m = Regex.Match(xml, @"<DisplayName>(.*?)</DisplayName>", RegexOptions.Singleline);
                if (m.Success && !m.Groups[1].Value.StartsWith("[SY]"))
                    xml = xml.Substring(0, m.Index) + "<DisplayName>[SY] " + m.Groups[1].Value + "</DisplayName>" +
                          xml.Substring(m.Index + m.Length);
                xml = xml.Replace("<Owner>0</Owner>", "<Owner>1</Owner>");
                return System.Text.Encoding.UTF8.GetBytes(xml);
            }
            catch (Exception ex) { Plugin.Log("PrepareInstalledBp failed: " + ex.Message); }
            return bytes;
        }

        // Decode an Octokit blob's content to raw bytes (the API base64-encodes binaries).
        private static byte[] BlobBytes(Blob b) =>
            b.Encoding == EncodingType.Base64 ? Convert.FromBase64String(b.Content)
                                              : System.Text.Encoding.UTF8.GetBytes(b.Content);

        // Content-addressed blob cache: a git blob SHA is the hash of its bytes, so a cached file under
        // that SHA is byte-identical and never stale. Details/diff/spawn/install of a version we've
        // already pulled read from disk - no GitHub download.
        private static string BlobCacheDir() => AppData("Shipyard", "blobs");
        private static byte[] CachedBlobBytes(GitHubClient client, string sha)
        {
            string fp = Path.Combine(BlobCacheDir(), sha + ".bin");
            try { if (File.Exists(fp)) return File.ReadAllBytes(fp); }
            catch { }   // cache read is best-effort: on any error fall through and fetch the blob fresh below
            byte[] data = BlobBytes(client.Git.Blob.Get(Auth.RepoOwner, Auth.RepoName, sha).GetAwaiter().GetResult());
            try { Directory.CreateDirectory(BlobCacheDir()); File.WriteAllBytes(fp, data); }
            catch (Exception ex) { Plugin.Log("blob cache write failed: " + ex.Message); }
            return data;
        }

        // Local install folder for a ship. Normally just the slug, but if that folder already holds
        // a DIFFERENT repo ship (same slug in another folder, per its shipyard.meta), disambiguate
        // with the folder path so the two don't silently overwrite each other.
        private static string InstallDirFor(string categoryShip)
        {
            string name = categoryShip.Substring(categoryShip.LastIndexOf('/') + 1);
            string dest = Path.Combine(BlueprintsLocal(), name);
            try
            {
                string meta = Path.Combine(dest, "shipyard.meta");
                if (Directory.Exists(dest) && File.Exists(meta))
                {
                    var m = Regex.Match(File.ReadAllText(meta), @"(?m)^path:\s*(.+)$");
                    if (m.Success && !string.Equals(m.Groups[1].Value.Trim(), RootSlash + categoryShip, StringComparison.OrdinalIgnoreCase))
                        return Path.Combine(BlueprintsLocal(), Slug(categoryShip.Replace('/', '-')));
                }
            }
            catch (Exception ex) { Plugin.Log("InstallDirFor failed: " + ex.Message); }
            return dest;
        }

        // Download one ship's bp.sbc + thumb.png from an already-fetched main tree into the local
        // library. Returns the file count; destName gets the (possibly disambiguated) folder name.
        private static int InstallFromTree(GitHubClient client, TreeResponse tree, string categoryShip,
            Dictionary<string, string> owners, out string destName)
        {
            string prefix = RootSlash + categoryShip + "/";
            string dest = InstallDirFor(categoryShip);
            destName = Path.GetFileName(dest);
            Directory.CreateDirectory(dest);
            int n = 0;
            foreach (var f in tree.Tree.Where(x => x.Type == TreeType.Blob && x.Path.StartsWith(prefix)))
            {
                string fname = f.Path.Substring(prefix.Length);
                if (fname != "bp.sbc" && fname != "thumb.png") continue;
                byte[] data = CachedBlobBytes(client, f.Sha);
                if (fname == "bp.sbc") data = PrepareInstalledBp(data);
                File.WriteAllBytes(Path.Combine(dest, fname), data);
                n++;
            }
            WriteMeta(dest, categoryShip, owners.TryGetValue(RootSlash + categoryShip, out var o) ? o : null);
            return n;
        }

        // Fast: just the list of ship paths under the top folder (one API call, no thumbnail downloads).
        // Used by the chat terminal (ls/cd/folders/ships) where thumbnails aren't needed.
        public static List<string> FetchShipPaths()
        {
            if (Auth.IsOffline) return LocalData().Ships.Select(s => s.CategoryShip).ToList();
            var client = Gh();
            var tree = client.Git.Tree.GetRecursive(Auth.RepoOwner, Auth.RepoName, "main").GetAwaiter().GetResult();
            return tree.Tree.Where(x => x.Type == TreeType.Blob && x.Path.StartsWith(RootSlash) && x.Path.EndsWith("/bp.sbc"))
                            .Select(x => x.Path.Substring(RootSlash.Length, x.Path.Length - RootSlash.Length - 7))
                            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Push a SHIPYARD ship to the Steam Workshop (per-user, optional; see WorkshopPush).
        // Always SYNCS the local copy from main first, then publishes that, so the Workshop item is
        // guaranteed to match the repo. (A stale local copy was the #1 silent no-op: Steam dedupes an
        // identical re-upload, so no change note / no new 'Updated' date appeared.) Workshop pushes are
        // repo-ships-only for exactly this reason - there's a single source of truth.
        public static void PushRepoShipToWorkshop(string categoryShip)
        {
            if (string.IsNullOrEmpty(categoryShip)) { ShipyardRunner.ShowMessage("Select a ship first."); return; }

            ShipyardRunner.RunWithBusyThen<string>(
                "Syncing " + categoryShip + " from the shipyard...",
                () =>
                {
                    var client = Gh();
                    var tree = client.Git.Tree.GetRecursive(Auth.RepoOwner, Auth.RepoName, "main").GetAwaiter().GetResult();
                    if (!tree.Tree.Any(x => x.Type == TreeType.Blob && x.Path == RootSlash + categoryShip + "/bp.sbc"))
                        throw new Exception("'" + categoryShip + "' isn't on main - can't push it to the Workshop.");
                    string destName;
                    InstallFromTree(client, tree, categoryShip, ReadCodeowners(client), out destName);
                    Plugin.Log("workshop sync: " + categoryShip + " -> local '" + destName + "'");
                    return destName;
                },
                destName =>
                {
                    // mapKey = the repo path (stable identity for the workshop.json entry).
                    WorkshopPush.Push(categoryShip, Path.Combine(BlueprintsLocal(), destName));
                });
        }

        // Chat parity: /sy workshop <ship> (resolves a repo ship, syncs, then pushes).
        public static void PushToWorkshopByQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) { ShipyardRunner.ShowMessage("Usage: /sy workshop <ship>"); return; }
            ResolveShipThen(query, s => PushRepoShipToWorkshop(s.CategoryShip));
        }

        // Install "PvP/Red-Ship_1" into %AppData%\SpaceEngineers\Blueprints\local\Red-Ship_1\
        public static void Install(string categoryShip, Action onDone = null)
        {
            ShipyardRunner.RunWithBusyThen<string>("Installing " + categoryShip + "...", () =>
            {
                string destName; int n;
                if (Auth.IsOffline)
                {
                    if (LocalBlueprintBytes(categoryShip) == null) return "Local ship not found: " + categoryShip;
                    n = LocalInstall(categoryShip, out destName);
                }
                else
                {
                    var client = Gh();
                    var tree = client.Git.Tree.GetRecursive(Auth.RepoOwner, Auth.RepoName, "main").GetAwaiter().GetResult();
                    if (!tree.Tree.Any(x => x.Type == TreeType.Blob && x.Path == RootSlash + categoryShip + "/bp.sbc"))
                        return "Ship not found on main: " + categoryShip;
                    n = InstallFromTree(client, tree, categoryShip, ReadCodeowners(client), out destName);
                }
                Plugin.Log("installed " + categoryShip + " -> " + destName + " (" + n + " files)");
                return "Installed '" + destName + "' (" + n + " files).\n" +
                       "It will appear under Blueprints (F10) after a refresh/reload.";
            },
            text => { ShipyardRunner.ShowResult(text); onDone?.Invoke(); });
        }

        // ---------------------------------------------------------------- review / merge ----

        // Open the Shipyard straight to the Review tab.
        public static void OpenPendingChanges()
        {
            // First boot / not configured: route to the right setup screen, consistent with OpenShipyard.
            if (!Auth.ModeChosen) { MyGuiSandbox.AddScreen(new ModeChooseScreen()); return; }
            if (!Auth.IsConfigured)
            { MyGuiSandbox.AddScreen(Auth.IsOffline ? (MyGuiScreenBase)new OfflineSetupScreen() : new SettingsScreen()); return; }
            ShipyardScreen.CloseActiveIfOpen();   // never stack Shipyard views

            // OFFLINE: no network / no PRs. Open the Review tab off the local working tree (its Prs list
            // is empty), consistent with every other offline entry point - never call Gh()/FetchAll here.
            if (Auth.IsOffline)
            {
                var local = LocalData();
                MyGuiSandbox.AddScreen(new ShipyardScreen(local.Ships, local.Prs, null, ShipyardScreen.Tab.Review));
                return;
            }

            ShipyardRunner.RunWithBusyThen<ShipyardData>(
                "Loading pending changes...",
                () => { try { return FetchAll(); }
                        catch (NotFoundException) { throw new Exception(RepoNotFoundMsg()); } },
                data => MyGuiSandbox.AddScreen(new ShipyardScreen(data.Ships, data.Prs, null, ShipyardScreen.Tab.Review)));
        }

        // ---------------------------------------------------------------- chat commands ----
        // Fuzzy-resolve a ship by name (substring on the full "Folder/Ship" path) then act on it.
        // Resolves from the bare path list (one API call, no thumbnail/tag downloads). The actions
        // these feed (install/spawn/diff) only need the ship's path.
        private static void ResolveShipThen(string query, Action<ShipEntry> onResolved)
        {
            string q = (query ?? "").Trim().ToLowerInvariant();
            if (q.Length == 0) { ShipyardRunner.ShowMessage("Name a ship, e.g. /sy install sudentor"); return; }
            ShipyardRunner.RunWithBusyThen<List<string>>(
                "Finding '" + query + "'...",
                () => FetchShipPaths(),
                paths =>
                {
                    var matches = paths.Where(p => p.ToLowerInvariant().Contains(q)).ToList();
                    if (matches.Count == 0) { ShipyardRunner.ShowMessage("No ship matches '" + query + "'."); return; }
                    string pick = matches[0];
                    if (matches.Count > 1)
                    {
                        var exact = matches.Where(p => string.Equals(
                            p.Substring(p.LastIndexOf('/') + 1), query.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                        if (exact.Count == 1) pick = exact[0];
                        else
                        {
                            var sb = new StringBuilder("Multiple ships match '" + query + "':\n");
                            foreach (var p in matches.Take(8)) sb.AppendLine("  " + p);
                            sb.AppendLine("Be more specific.");
                            ShipyardRunner.ShowMessage(sb.ToString());
                            return;
                        }
                    }
                    int sl = pick.LastIndexOf('/');
                    onResolved(new ShipEntry
                    {
                        CategoryShip = pick,
                        Folder = sl > 0 ? pick.Substring(0, sl) : "",
                        Name = sl > 0 ? pick.Substring(sl + 1) : pick
                    });
                });
        }

        public static void InstallByQuery(string query) => ResolveShipThen(query, s => Install(s.CategoryShip));

        public static void SpawnByQuery(string query)
        {
            if (MyAPIGateway.Session == null) { ShipyardRunner.ShowMessage("Spawning requires being in a world."); return; }
            ResolveShipThen(query, s => SpawnFromRepo(s.CategoryShip));
        }

        public static void CheckOutByQuery(string query)
        {
            if (MyAPIGateway.Session == null) { ShipyardRunner.ShowMessage("Checkout requires being in a world (it pastes the ship)."); return; }
            ResolveShipThen(query, s => CheckOut(s));
        }

        // /sy commit [ship]: commit the grid you're looking at to a ship YOU have checked out.
        public static void CommitByQuery(string query)
        {
            if (MyAPIGateway.Session == null) { ShipyardRunner.ShowMessage("Committing requires being in a world."); return; }
            IMyCubeGrid grid;
            if (!TryGetLookedAtGrid(out grid)) { ShipyardRunner.ShowMessage("Look at the ship you're working on, then /sy commit again."); return; }
            if (Auth.IsOffline) { LocalCommitResolved(query, grid); return; }   // offline: commit to local main
            CommitResolved(query, grid);
        }

        // Capture 'grid' and commit it to the caller's checked-out ship. query optionally narrows when
        // more than one ship is checked out. Used by /sy commit and by the menu Commit (no tile selected).
        public static void CommitResolved(string query, IMyCubeGrid grid)
        {
            ShipyardRunner.RunWithBusyThen<List<string>>(
                "Finding your checkout...",
                () =>
                {
                    string me = Auth.Login;
                    return FetchCheckouts(Gh())
                        .Where(kv => string.Equals(kv.Value, me, StringComparison.OrdinalIgnoreCase))
                        .Select(kv => kv.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                },
                mine =>
                {
                    if (mine.Count == 0)
                    { ShipyardRunner.ShowMessage("You have no checked-out ships.\nCheck one out first (Browse -> Check Out, or /sy checkout <ship>)."); return; }

                    string q = (query ?? "").Trim().ToLowerInvariant();
                    if (q.Length > 0)
                    {
                        var hits = mine.Where(s => s.ToLowerInvariant().Contains(q) ||
                                                   LastSeg(s).ToLowerInvariant().Contains(q)).ToList();
                        if (hits.Count == 1) { CommitGrid(ShipEntryOf(hits[0]), grid); return; }
                        if (hits.Count > 1) { ShipyardRunner.ShowMessage("Several checkouts match '" + query + "':\n  " + string.Join("\n  ", hits) + "\nBe more specific."); return; }
                    }
                    if (mine.Count == 1) { CommitGrid(ShipEntryOf(mine[0]), grid); return; }

                    // Multiple checkouts, no query match: try the grid's name as a last hint.
                    string gn = (grid.CustomName ?? grid.DisplayName ?? "").ToLowerInvariant();
                    var byName = mine.Where(s => gn.Contains(LastSeg(s).ToLowerInvariant())).ToList();
                    if (byName.Count == 1) { CommitGrid(ShipEntryOf(byName[0]), grid); return; }

                    ShipyardRunner.ShowMessage("You have several ships checked out:\n  " + string.Join("\n  ", mine) +
                        "\nSay which one: /sy commit <ship>");
                });
        }

        private static string LastSeg(string cs) { int s = cs.LastIndexOf('/'); return s >= 0 ? cs.Substring(s + 1) : cs; }
        private static ShipEntry ShipEntryOf(string cs)
        {
            int sl = cs.LastIndexOf('/');
            return new ShipEntry { CategoryShip = cs, Folder = sl > 0 ? cs.Substring(0, sl) : "", Name = LastSeg(cs) };
        }

        public static void ReleaseByQuery(string query) =>
            ResolveShipThen(query, s => ShipyardRunner.Confirm("RELEASE CHECKOUT",
                "Delete the work branch for '" + s.CategoryShip + "'?\nCommitted WIP on it is LOST (merged work is safe on main).",
                ok => { if (ok) ReleaseCheckout(s); }));

        public static void FinishByQuery(string query) => ResolveShipThen(query, s => FinishCheckout(s));

        // ---- chat parity: ship-by-query verbs ----
        public static void DetailsByQuery(string query) => ResolveShipThen(query, s => ShowShipDetails(s));
        public static void DeleteByQuery(string query) => ResolveShipThen(query, s =>
            ShipyardRunner.Confirm("CONFIRM DELETE",
                Auth.IsOffline ? "Delete '" + s.CategoryShip + "' from your local shipyard?"
                               : "Delete '" + s.CategoryShip + "' from main for everyone?",
                ok => { if (ok) DeleteShip(s); }));
        public static void ClipboardByQuery(string query)
        {
            if (MyAPIGateway.Session == null) { ShipyardRunner.ShowMessage("Pasting requires being in a world."); return; }
            ResolveShipThen(query, s => LoadToClipboard(s.CategoryShip));
        }

        // "<local> <folder> [tag, tag]" -> publish a local blueprint as a NEW ship.
        public static void PublishByChat(string arg)
        {
            var parts = (arg ?? "").Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) { ShipyardRunner.ShowMessage("Usage: /sy publish <local blueprint> <folder> [tags]"); return; }
            var tags = parts.Length > 2 ? parts[2].Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList() : new List<string>();
            AddNewShip(parts[0].Trim(), parts[0].Trim(), parts[1].Trim(), tags);
        }

        // ---- chat parity: PR-by-number verbs ----
        // Resolve an open PR by number, then act on it (review/merge/diff need the full PrEntry).
        private static void WithPr(int number, bool needWorld, Action<PrEntry> act)
        {
            if (needWorld && MyAPIGateway.Session == null) { ShipyardRunner.ShowMessage("That requires being in a world."); return; }
            ShipyardRunner.RunWithBusyThen<List<PrEntry>>("Finding PR #" + number + "...", () => FetchPullRequests(),
                prs =>
                {
                    var pr = prs.FirstOrDefault(p => p.Number == number);
                    if (pr == null) { ShipyardRunner.ShowMessage("No open PR #" + number + "."); return; }
                    act(pr);
                });
        }
        public static void MergeByNumber(int n) => WithPr(n, false, Merge);
        public static void RejectByNumber(int n) => WithPr(n, false, pr => ShipyardRunner.Confirm("REJECT PR",
            "Reject (close) PR #" + pr.Number + " without merging? The branch will be deleted.",
            ok => { if (ok) ClosePr(pr); }));
        public static void VisualDiffByNumber(int n) => WithPr(n, true, VisualDiff);
        public static void InstallPrByNumber(int n) => WithPr(n, false, InstallPrVersion);
        public static void PrDetailsByNumber(int n) => WithPr(n, false, ShowPrDetails);

        // Highlight changes (vs main) on the grid the player is looking at. Resolves the repo ship by
        // the optional query, else by the looked-at grid's name.
        public static void DiffLookedAt(string query)
        {
            if (MyAPIGateway.Session == null) { ShipyardRunner.ShowMessage("Diff requires being in a world."); return; }
            IMyCubeGrid grid;
            if (!TryGetLookedAtGrid(out grid)) { ShipyardRunner.ShowMessage("Look at a ship first, then run /sy diff."); return; }
            string q = !string.IsNullOrWhiteSpace(query) ? query : (grid.CustomName ?? grid.DisplayName);
            ResolveShipThen(q, s => HighlightCurrent(s, grid));
        }

        // Parse bp.sbc bytes into its cube grids (throws operator-facing text on bad data).
        private static MyObjectBuilder_CubeGrid[] ParseGrids(byte[] bytes, string what)
        {
            MyObjectBuilder_Definitions defs; ulong sz;
            if (bytes == null || !MyObjectBuilderSerializer.DeserializeXML(bytes, out defs, out sz) ||
                defs.ShipBlueprints == null || defs.ShipBlueprints.Length == 0)
                throw new Exception("Couldn't parse the blueprint for " + what + ".");
            var grids = defs.ShipBlueprints[0].CubeGrids;
            if (grids == null || grids.Length == 0) throw new Exception("Blueprint has no grids.");
            return grids;
        }

        // Fetch a ship's grids from main (background-thread work for the spawn/clipboard/projector paths).
        private static MyObjectBuilder_CubeGrid[] FetchGrids(string categoryShip)
        {
            var client = Gh();
            byte[] bytes = GetBlueprintBytes(client, "main", categoryShip);
            if (bytes == null) throw new Exception("Couldn't read bp.sbc for " + categoryShip);
            return ParseGrids(bytes, categoryShip);
        }

        // Make the spawning player the owner/builder of every block. Published ships are scrubbed to
        // "Nobody" (Owner=0), which would otherwise spawn unowned — turrets off, no terminal access.
        private static void StampOwnership(MyObjectBuilder_CubeGrid[] grids)
        {
            long id = 0;
            try { id = MyAPIGateway.Session?.Player?.IdentityId ?? 0; }
            catch { }   // no local identity available -> id stays 0 and we return below (nothing to stamp)
            if (id == 0) return;
            foreach (var g in grids)
                if (g.CubeBlocks != null)
                    foreach (var b in g.CubeBlocks) { b.Owner = id; b.BuiltBy = id; }
        }

        // Download a ship from main and direct-spawn it ~120m ahead of the camera (dynamic, placeable).
        public static void SpawnFromRepo(string categoryShip)
        {
            ShipyardRunner.RunWithBusyThen<MyObjectBuilder_CubeGrid[]>(
                "Loading " + categoryShip + "...",
                () => Auth.IsOffline ? LocalGrids(categoryShip) : FetchGrids(categoryShip),
                grids =>
                {
                    StampOwnership(grids);
                    var ent = SpawnDiffGrids(grids, categoryShip, false);
                    if (ent != null) ShipyardRunner.ShowResult("Spawned '" + categoryShip + "' ~" + (int)SpawnAheadMeters + "m ahead.");
                });
        }

        // MAIN THREAD: stamp ownership + attach grids to the game clipboard in interactive paste
        // mode. The exact flow of the native F10 paste (MyGuiBlueprintScreen_Reworked.
        // CopyBlueprintPrefabToClipboard): drag-point + drag-length from the primary grid's bounding
        // sphere so the ship hangs off the cursor instead of appearing at its saved world position.
        // Returns an operator-facing error, or null on success.
        private static string PasteGrids(MyObjectBuilder_CubeGrid[] grids, string label)
        {
            try
            {
                if (MySession.Static == null || !MySession.Static.IsCopyPastingEnabled)
                    return "Pasting is not enabled in this world.";
                var clip = MyClipboardComponent.Static != null ? MyClipboardComponent.Static.Clipboard : null;
                if (clip == null) return "Clipboard unavailable (are you in a world?).";
                if (!grids[0].PositionAndOrientation.HasValue)
                    return "This blueprint has no position data - use Spawn instead.";

                StampOwnership(grids);
                BoundingSphere sphere = grids[0].CalculateBoundingSphere();
                var po = grids[0].PositionAndOrientation.Value;
                MatrixD world = MatrixD.CreateWorld(po.Position, po.Forward, po.Up);
                Matrix invNorm = Matrix.Normalize(MatrixD.Invert(world));
                BoundingSphere worldSphere = sphere.Transform(world);
                Vector3 dragPointDelta = Vector3.TransformNormal((Vector3)(Vector3D)po.Position - worldSphere.Center, invNorm);
                float dragVectorLength = sphere.Radius + ClipboardDragPad;

                clip.SetGridFromBuilders(grids, dragPointDelta, dragVectorLength);
                clip.ShowModdedBlocksWarning = false;
                MyClipboardComponent.Static.Paste();
                Plugin.Log("clipboard paste: " + label + " (" + grids.Length + " grids)");
                return null;
            }
            catch (Exception ex)
            { Plugin.Log("PasteGrids failed: " + ex); return "Clipboard load failed: " + ex.Message; }
        }

        // Fetch a repo ship into the game's paste clipboard so the player places it interactively.
        public static void LoadToClipboard(string categoryShip)
        {
            ShipyardRunner.RunWithBusyThen<MyObjectBuilder_CubeGrid[]>(
                "Loading " + categoryShip + " to clipboard...",
                () => Auth.IsOffline ? LocalGrids(categoryShip) : FetchGrids(categoryShip),
                grids => { string err = PasteGrids(grids, categoryShip); if (err != null) ShipyardRunner.ShowMessage(err); });
        }

        // ---------------------------------------------------------------- checkout / WIP ----
        // One canonical work branch per ship: checkout/<folder>/<ship>. The branch's existence IS
        // the lock; its head commit's "... by @login" message + date say who and since when.

        private static string CheckoutBranch(string categoryShip) => "checkout/" + categoryShip;

        // Patch the session cache's checkout state for one ship (instant, no refetch) + redraw the open
        // menu, so Publish -> Update reflects a just-created checkout the moment it's confirmed.
        private static void MarkCheckedOut(string cs, string by)
        {
            try
            {
                var e = _cache?.Ships?.FirstOrDefault(s => string.Equals(s.CategoryShip, cs, StringComparison.OrdinalIgnoreCase));
                if (e != null) e.CheckedOutBy = by;
                ShipyardRunner.InvokeOnMain(() => ShipyardScreen.ApplyData(_cache));
            }
            catch (Exception ex) { Plugin.Log("MarkCheckedOut failed: " + ex.Message); }
        }

        public class CheckoutInfo { public string User; public string When; public string Sha; }

        // Branch SHA each ship was last pasted from this session (the stale-commit overwrite guard).
        private static readonly Dictionary<string, string> _checkoutBase =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Who has the ship checked out (latest activity), or null if it isn't checked out.
        // Resolve via the matching-refs list (see ResolveBranchSha), not Git.Reference.Get: here it
        // matters for correctness, since a swallowed failure would report a locked ship as free and let
        // two people check it out at once.
        private static CheckoutInfo GetCheckout(GitHubClient client, string categoryShip)
        {
            try
            {
                string sha = ResolveBranchSha(client, CheckoutBranch(categoryShip));
                if (sha == null) return null;   // no checkout branch -> not checked out
                var c = client.Git.Commit.Get(Auth.RepoOwner, Auth.RepoName, sha).GetAwaiter().GetResult();
                var m = Regex.Match(c.Message ?? "", @"by @(\S+)");
                return new CheckoutInfo
                {
                    User = m.Success ? m.Groups[1].Value : (c.Author != null ? c.Author.Name : "?"),
                    When = c.Author != null ? c.Author.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "?",
                    Sha = sha
                };
            }
            catch (NotFoundException) { return null; }
        }

        // "Check out": lock the ship for collaborative work and put its WIP version on your clipboard.
        // If it's already checked out, offer to JOIN (paste their latest WIP). Hard lock, no silent
        // parallel checkouts. Independent variants go through Publish-as-NEW instead.
        public static void CheckOut(ShipEntry ship)
        {
            string cs = ship.CategoryShip;
            ShipyardRunner.RunWithBusyModalThen<object[]>(
                "Checking out " + cs + "...",
                () =>
                {
                    var client = Gh();
                    var existing = GetCheckout(client, cs);
                    if (existing != null) return new object[] { "locked", existing };

                    // Create the lock: an empty commit on a new checkout branch at main's head.
                    string login = client.User.Current().GetAwaiter().GetResult().Login;
                    var mainRef = client.Git.Reference.Get(Auth.RepoOwner, Auth.RepoName, "heads/main").GetAwaiter().GetResult();
                    var mainCommit = client.Git.Commit.Get(Auth.RepoOwner, Auth.RepoName, mainRef.Object.Sha).GetAwaiter().GetResult();
                    var commit = client.Git.Commit.Create(Auth.RepoOwner, Auth.RepoName,
                        new NewCommit("checkout: " + cs + " by @" + login, mainCommit.Tree.Sha, mainRef.Object.Sha)).GetAwaiter().GetResult();
                    client.Git.Reference.Create(Auth.RepoOwner, Auth.RepoName,
                        new NewReference("refs/heads/" + CheckoutBranch(cs), commit.Sha)).GetAwaiter().GetResult();
                    byte[] bytes = GetBlueprintFileBytes(client, CheckoutBranch(cs), cs, "bp.sbc");
                    Plugin.Log("checked out " + cs + " by " + login);
                    return new object[] { "new", ParseGrids(bytes, cs), commit.Sha };
                },
                res =>
                {
                    if ((string)res[0] == "new")
                    {
                        _checkoutBase[cs] = (string)res[2];
                        // Reflect the new checkout in the cache BEFORE the confirmation, so the moment the
                        // user acknowledges it, Publish -> Update already lists this ship.
                        MarkCheckedOut(cs, Auth.Login);
                        string err = PasteGrids((MyObjectBuilder_CubeGrid[])res[1], "checkout " + cs);
                        // On the clipboard now - close the menu so the player can place it right away.
                        if (err == null) ShipyardScreen.CloseActiveIfOpen();
                        ShipyardRunner.ShowResult(err ?? ("Checked out '" + cs + "' - it's on your clipboard.\n" +
                            "Paste to place it, then build. Commit with 'Commit looked-at ship',\n" +
                            "then publish it from Publish -> Update a checked-out ship."));
                        return;
                    }
                    var info = (CheckoutInfo)res[1];
                    ShipyardRunner.Confirm("ALREADY CHECKED OUT",
                        "'" + cs + "' is checked out by @" + info.User + "\n(last activity " + info.When + ").\n" +
                        "JOIN their checkout? Their latest WIP lands on your clipboard.\n" +
                        "(For an independent variant, cancel and use 'Publish as NEW' instead.)",
                        ok => { if (ok) JoinCheckout(cs); });
                });
        }

        private static void JoinCheckout(string cs)
        {
            ShipyardRunner.RunWithBusyModalThen<object[]>(
                "Joining the checkout of " + cs + "...",
                () =>
                {
                    var client = Gh();
                    // Resolve the branch's HEAD sha via the matching-refs list (see ResolveBranchSha).
                    string headSha = ResolveBranchSha(client, CheckoutBranch(cs));
                    byte[] bytes = GetBlueprintFileBytes(client, CheckoutBranch(cs), cs, "bp.sbc");
                    return new object[] { ParseGrids(bytes, cs), headSha };
                },
                res =>
                {
                    _checkoutBase[cs] = (string)res[1];
                    string err = PasteGrids((MyObjectBuilder_CubeGrid[])res[0], "join checkout " + cs);
                    if (err == null) ShipyardScreen.CloseActiveIfOpen();
                    ShipyardRunner.ShowResult(err ?? ("Joined the checkout of '" + cs + "' - the latest WIP is on your clipboard."));
                });
        }

        // Capture the aimed grid (+ subgrids, exactly like the game's Ctrl+C group-copy) and commit
        // the snapshot to the ship's checkout branch. MAIN THREAD entry (capture), then background.
        public static void CommitLookedAt(ShipEntry ship)
        {
            IMyCubeGrid grid;
            if (!TryGetLookedAtGrid(out grid)) { ShipyardRunner.ShowMessage("Look at the ship you're working on, then Commit again."); return; }
            CommitGrid(ship, grid);
        }

        // Capture a specific grid (+ its subgrid group) and commit it to the ship's checkout branch.
        // MAIN THREAD (the CopyGroup capture touches live entities), then the upload runs in background.
        public static void CommitGrid(ShipEntry ship, IMyCubeGrid grid)
        {
            string cs = ship.CategoryShip;
            MyObjectBuilder_CubeGrid[] grids;
            try
            {
                var clip = MyClipboardComponent.Static != null ? MyClipboardComponent.Static.Clipboard : null;
                var cube = grid as Sandbox.Game.Entities.MyCubeGrid;
                if (clip == null || cube == null) { ShipyardRunner.ShowMessage("Couldn't capture that grid."); return; }
                clip.CopyGroup(cube, GridLinkTypeEnum.Logical);
                grids = clip.CopiedGrids != null ? clip.CopiedGrids.ToArray() : null;
                clip.Deactivate();   // capture only — don't enter paste mode
                if (grids == null || grids.Length == 0) { ShipyardRunner.ShowMessage("Couldn't capture that grid."); return; }
            }
            catch (Exception ex) { Plugin.Log("grid capture failed: " + ex); ShipyardRunner.ShowMessage("Capture failed: " + ex.Message); return; }

            int blocks = grids.Sum(g => g.CubeBlocks != null ? g.CubeBlocks.Count : 0);
            CommitGrids(cs, grids, blocks, force: false);
        }

        // OFFLINE update: capture the looked-at grid and commit it OVER an existing local ship's bp.sbc,
        // straight onto the local main branch (no branching for local in 1.0). Capture is main-thread
        // (touches live entities); serialize + scrub + git commit run in the background like online.
        public static void LocalCommitGrid(string cs, IMyCubeGrid grid)
        {
            MyObjectBuilder_CubeGrid[] grids;
            try
            {
                var clip = MyClipboardComponent.Static != null ? MyClipboardComponent.Static.Clipboard : null;
                var cube = grid as Sandbox.Game.Entities.MyCubeGrid;
                if (clip == null || cube == null) { ShipyardRunner.ShowMessage("Couldn't capture that grid."); return; }
                clip.CopyGroup(cube, GridLinkTypeEnum.Logical);
                grids = clip.CopiedGrids != null ? clip.CopiedGrids.ToArray() : null;
                clip.Deactivate();   // capture only
                if (grids == null || grids.Length == 0) { ShipyardRunner.ShowMessage("Couldn't capture that grid."); return; }
            }
            catch (Exception ex) { Plugin.Log("local capture failed: " + ex); ShipyardRunner.ShowMessage("Capture failed: " + ex.Message); return; }

            ShipyardRunner.RunWithBusyThen<string>(
                "Committing " + cs + " to your local shipyard...",
                () =>
                {
                    byte[] old = LocalBlueprintBytes(cs);
                    var oldMap = old != null ? BlocksByPositionPrimary(old) : new Dictionary<string, MyObjectBuilder_CubeBlock>();
                    var newMap = BlocksFromPrimaryGrid(grids);
                    int added, changed, deleted, settings;
                    BlockDiffCounts(oldMap, newMap, out added, out changed, out deleted, out settings);
                    string delta = "+" + added + " added  ~" + changed + " changed  -" + deleted + " deleted"
                                 + (settings > 0 ? "  ±" + settings + " data" : "");

                    ScrubGrids(grids);   // zero world position / GPS / ownership before it touches the repo
                    var bp = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_ShipBlueprintDefinition>();
                    bp.Id = new MyDefinitionId(new MyObjectBuilderType(typeof(MyObjectBuilder_ShipBlueprintDefinition)),
                        cs.Substring(cs.LastIndexOf('/') + 1));
                    bp.CubeGrids = grids; bp.RespawnShip = false; bp.DisplayName = Auth.LocalAuthor;
                    var defs = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Definitions>();
                    defs.ShipBlueprints = new[] { bp };
                    string xml;
                    using (var ms = new MemoryStream())
                    {
                        if (!MyObjectBuilderSerializerKeen.SerializeXML(ms, defs)) throw new Exception("Couldn't serialize the captured grid.");
                        xml = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                    }
                    xml = ScrubBp(xml);
                    LocalSaveShip(cs, System.Text.Encoding.UTF8.GetBytes(xml), null, null, "update: " + cs + " (" + delta + ") offline");
                    Plugin.Log("local commit " + cs + " (" + delta + ")");
                    return "Committed to your local shipyard:\n  " + cs + "\n  " + delta + ".";
                },
                msg => { ShipyardRunner.ShowResult(msg); ShipyardScreen.CloseActiveIfOpen(); OpenShipyard(null); });
        }

        // /sy commit offline: pick the local ship to update (by query, else the looked-at grid's name).
        private static void LocalCommitResolved(string query, IMyCubeGrid grid)
        {
            var ships = LocalData().Ships;
            if (ships.Count == 0) { ShipyardRunner.ShowMessage("No local ships yet - save one first (Publish -> Save as a NEW ship)."); return; }
            string q = (query ?? "").Trim().ToLowerInvariant();
            List<ShipEntry> hits;
            if (q.Length > 0)
                hits = ships.Where(s => s.CategoryShip.ToLowerInvariant().Contains(q) || s.Name.ToLowerInvariant().Contains(q)).ToList();
            else
            {
                string gn = (grid.CustomName ?? grid.DisplayName ?? "").ToLowerInvariant();
                hits = ships.Where(s => gn.Contains(s.Name.ToLowerInvariant())).ToList();
            }
            if (hits.Count == 1) { LocalCommitGrid(hits[0].CategoryShip, grid); return; }
            if (hits.Count == 0) { ShipyardRunner.ShowMessage("No local ship matches '" + query + "'. Try /sy commit <ship name>."); return; }
            ShipyardRunner.ShowMessage("Several local ships match:\n  " + string.Join("\n  ", hits.Take(8).Select(s => s.CategoryShip)) + "\nBe more specific: /sy commit <name>.");
        }

        // Last committed block map per checkout so a commit's diff is computed against
        // it with NO network/parse (only the first commit of a session reads the old version remotely).
        private static readonly Dictionary<string, Dictionary<string, MyObjectBuilder_CubeBlock>> _lastCommitBlocks =
            new Dictionary<string, Dictionary<string, MyObjectBuilder_CubeBlock>>(StringComparer.OrdinalIgnoreCase);

        private static bool CellsOverlap(Vector3I amin, Vector3I amax, Vector3I bmin, Vector3I bmax) =>
            amin.X <= bmax.X && amax.X >= bmin.X && amin.Y <= bmax.Y && amax.Y >= bmin.Y && amin.Z <= bmax.Z && amax.Z >= bmin.Z;

        // Added / changed / deleted counts of newB vs oldB (pivot-aligned), for the commit summary.
        // Same-cell, different-block = "changed". A swap to a DIFFERENT-SIZE block lands at a different
        // Min (so it first reads as add+delete). We then fold any deleted+added pair whose CELL
        // FOOTPRINTS overlap into one "changed", matching the visual diff's ORANGE 'replaced'.
        private static void BlockDiffCounts(Dictionary<string, MyObjectBuilder_CubeBlock> oldB,
            Dictionary<string, MyObjectBuilder_CubeBlock> newB, out int added, out int changed, out int deleted, out int settings)
        {
            changed = 0; settings = 0;
            int m; Vector3I off = RecoverOffset(PosSubs(oldB), PosSubs(newB), out m);
            var oldShift = ShiftKeys(oldB, off);   // old keyed into the new grid's frame

            var addedBlocks = new List<MyObjectBuilder_CubeBlock>();
            var deletedBlocks = new List<MyObjectBuilder_CubeBlock>();
            foreach (var kv in newB)
            {
                MyObjectBuilder_CubeBlock o;
                if (!oldShift.TryGetValue(kv.Key, out o)) { addedBlocks.Add(kv.Value); continue; }
                if (!string.Equals(kv.Value.SubtypeName, o.SubtypeName, StringComparison.Ordinal) ||
                    ColorDiffers(kv.Value.ColorMaskHSV, o.ColorMaskHSV)) changed++;
                else if (SettingsDiffer(o, kv.Value)) settings++;   // same block, new custom data/settings
            }
            foreach (var kv in oldShift) if (!newB.ContainsKey(kv.Key)) deletedBlocks.Add(kv.Value);

            // Fold overlapping deleted/added pairs (a same-mount size swap) into "changed".
            var addExt = new Vector3I[addedBlocks.Count][];
            for (int i = 0; i < addedBlocks.Count; i++) { Vector3I mn, mx; ObExtent(addedBlocks[i], out mn, out mx); addExt[i] = new[] { mn, mx }; }
            var usedAdded = new bool[addedBlocks.Count];
            int collapsed = 0;
            foreach (var d in deletedBlocks)
            {
                Vector3I dmn, dmx; ObExtent(d, out dmn, out dmx); dmn += off; dmx += off;   // old -> new frame
                for (int i = 0; i < addExt.Length; i++)
                {
                    if (usedAdded[i]) continue;
                    if (CellsOverlap(dmn, dmx, addExt[i][0], addExt[i][1])) { usedAdded[i] = true; collapsed++; break; }
                }
            }
            added = addedBlocks.Count - collapsed;
            deleted = deletedBlocks.Count - collapsed;
            changed += collapsed;
        }

        private static void CommitGrids(string cs, MyObjectBuilder_CubeGrid[] grids, int blocks, bool force)
        {
            ShipyardRunner.RunWithBusyThen<object[]>(
                "Committing to " + CheckoutBranch(cs) + "...",
                () =>
                {
                    var client = Gh();
                    string owner = Auth.RepoOwner, repo = Auth.RepoName, branch = CheckoutBranch(cs);
                    string login = Auth.Login;   // stored at sign-in; no User.Current round-trip
                    if (string.IsNullOrEmpty(login)) { try { login = client.User.Current().GetAwaiter().GetResult().Login; } catch (Exception ex) { Plugin.Log("login lookup (User.Current) failed: " + ex.Message); } }

                    Reference branchRef;
                    try { branchRef = client.Git.Reference.Get(owner, repo, "heads/" + branch).GetAwaiter().GetResult(); }
                    catch (NotFoundException)
                    { throw new Exception("'" + cs + "' isn't checked out.\nCheck it out first - that's what creates the work branch."); }

                    // Stale-head guard: someone committed since this player last pasted the WIP. Ship
                    // snapshots can't be merged, so committing over it would silently discard their work.
                    // baseSha == null means we have NO record of what this player last pasted (they didn't
                    // Check Out / Join in this process - e.g. restarted the game), so we can't prove the
                    // branch hasn't advanced. Treat that as unknown and require an explicit confirm rather
                    // than silently overwriting a concurrent committer's WIP.
                    string baseSha; _checkoutBase.TryGetValue(cs, out baseSha);
                    if (!force && baseSha != null && branchRef.Object.Sha != baseSha)
                        return new object[] { "stale", GetCheckout(client, cs) };
                    if (!force && baseSha == null)
                        return new object[] { "unknown-base", GetCheckout(client, cs) };

                    // Captured straight from the world -> carries world coordinates; zero them.
                    ScrubGrids(grids);

                    // Wrap the captured grids in a ShipBlueprint exactly like the game's Ctrl+B.
                    var bp = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_ShipBlueprintDefinition>();
                    bp.Id = new MyDefinitionId(new MyObjectBuilderType(typeof(MyObjectBuilder_ShipBlueprintDefinition)),
                        cs.Substring(cs.LastIndexOf('/') + 1));
                    bp.CubeGrids = grids;
                    bp.RespawnShip = false;
                    bp.DisplayName = login;
                    var defs = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Definitions>();
                    defs.ShipBlueprints = new[] { bp };
                    string xml;
                    using (var ms = new MemoryStream())
                    {
                        if (!MyObjectBuilderSerializerKeen.SerializeXML(ms, defs))
                            throw new Exception("Couldn't serialize the captured grid.");
                        xml = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                    }
                    xml = ScrubBp(xml);

                    // Fire the SLOW bp.sbc upload + the head-commit read concurrently; do the diff (pure
                    // in-memory) while they run, so the upload no longer waits behind reads + a download.
                    var bpBlobTask = client.Git.Blob.Create(owner, repo, new NewBlob { Content = xml, Encoding = EncodingType.Utf8 });
                    var headCommitTask = client.Git.Commit.Get(owner, repo, branchRef.Object.Sha);

                    var newMap = BlocksFromPrimaryGrid(grids);
                    Dictionary<string, MyObjectBuilder_CubeBlock> oldMap;
                    if (!_lastCommitBlocks.TryGetValue(cs, out oldMap))   // first commit this session
                    {
                        byte[] oldBytes = GetBlueprintBytes(client, branch, cs);
                        oldMap = oldBytes != null ? BlocksByPositionPrimary(oldBytes) : new Dictionary<string, MyObjectBuilder_CubeBlock>();
                    }
                    int added, changed, deleted, settings;
                    BlockDiffCounts(oldMap, newMap, out added, out changed, out deleted, out settings);
                    string delta = "+" + added + " added  ~" + changed + " changed  -" + deleted + " deleted"
                                 + (settings > 0 ? "  ±" + settings + " data" : "");

                    var headCommit = headCommitTask.GetAwaiter().GetResult();
                    var bpSha = bpBlobTask.GetAwaiter().GetResult().Sha;
                    var tree = new NewTree { BaseTree = headCommit.Tree.Sha };
                    tree.Tree.Add(new NewTreeItem { Path = RootSlash + cs + "/bp.sbc", Mode = "100644", Type = TreeType.Blob, Sha = bpSha });
                    var treeResp = client.Git.Tree.Create(owner, repo, tree).GetAwaiter().GetResult();
                    var commit = client.Git.Commit.Create(owner, repo,
                        new NewCommit("wip: " + cs + " (" + delta + ") by @" + login, treeResp.Sha, branchRef.Object.Sha)).GetAwaiter().GetResult();
                    client.Git.Reference.Update(owner, repo, "heads/" + branch, new ReferenceUpdate(commit.Sha, force)).GetAwaiter().GetResult();
                    _lastCommitBlocks[cs] = newMap;   // next commit diffs against this with no network/parse
                    Plugin.Log("committed " + cs + " (" + delta + ") to " + branch);
                    return new object[] { "ok", commit.Sha, delta };
                },
                res =>
                {
                    if ((string)res[0] == "stale")
                    {
                        var who = (CheckoutInfo)res[1];
                        ShipyardRunner.Confirm("BRANCH MOVED",
                            "@" + (who != null ? who.User : "someone") + " committed to this checkout since you last pasted it" +
                            (who != null ? "\n(last activity " + who.When + ")." : ".") + "\n" +
                            "Committing now OVERWRITES their version with yours.\n" +
                            "Overwrite? (Cancel keeps theirs - re-Check Out to load it.)",
                            ok => { if (ok) CommitGrids(cs, grids, blocks, force: true); });
                        return;
                    }
                    if ((string)res[0] == "unknown-base")
                    {
                        var who = (CheckoutInfo)res[1];
                        ShipyardRunner.Confirm("CONFIRM COMMIT",
                            "You didn't Check Out / Join '" + cs + "' in this session, so the Shipyard can't tell\n" +
                            "whether this checkout has advanced since you last had it" +
                            (who != null ? "\n(last activity " + who.When + " by @" + who.User + ")." : ".") + "\n" +
                            "Committing now OVERWRITES whatever is on the work branch with yours.\n" +
                            "Commit anyway? (Cancel and re-Check Out to load the latest WIP first.)",
                            ok => { if (ok) CommitGrids(cs, grids, blocks, force: true); });
                        return;
                    }
                    _checkoutBase[cs] = (string)res[1];
                    ShipyardRunner.ShowResult("Committed to " + CheckoutBranch(cs) + ":\n  " + (string)res[2] + ".\n" +
                        "Keep building and committing, then publish it from Publish -> Update a checked-out ship.");
                });
        }

        // Open (or surface) the PR that publishes this checkout to main. Merging releases the lock.
        public static void FinishCheckout(ShipEntry ship)
        {
            string cs = ship.CategoryShip;
            RunRepoOp("Opening PR for " + CheckoutBranch(cs) + "...", () =>
            {
                var client = Gh();
                string branch = CheckoutBranch(cs);
                try { client.Git.Reference.Get(Auth.RepoOwner, Auth.RepoName, "heads/" + branch).GetAwaiter().GetResult(); }
                catch (NotFoundException) { throw new Exception("'" + cs + "' isn't checked out - nothing to publish."); }
                var open = client.PullRequest.GetAllForRepository(Auth.RepoOwner, Auth.RepoName,
                    new PullRequestRequest { State = ItemStateFilter.Open, Head = Auth.RepoOwner + ":" + branch }).GetAwaiter().GetResult();
                if (open.Count > 0) return "PR #" + open[0].Number + " is already open for this checkout - see the Review tab.";
                var pr = client.PullRequest.Create(Auth.RepoOwner, Auth.RepoName,
                    new NewPullRequest(cs.Substring(cs.LastIndexOf('/') + 1) + ": checkout update", branch, "main")).GetAwaiter().GetResult();
                Plugin.Log("checkout PR #" + pr.Number + " (" + cs + ")");
                return "PR #" + pr.Number + " opened - review/merge it in the Review tab.\nMerging releases the checkout.";
            });
        }

        // Delete the work branch without merging (abandon). Soft-gated to the checkout's last
        // committer or the ship's owner so a third party can't yank someone's lock by accident.
        public static void ReleaseCheckout(ShipEntry ship)
        {
            string cs = ship.CategoryShip;
            RunRepoOp("Releasing the checkout of " + cs + "...", () =>
            {
                var client = Gh();
                var who = GetCheckout(client, cs);
                if (who == null) throw new Exception("'" + cs + "' isn't checked out.");
                string me = Auth.Login;   // stored at sign-in; fall back to the API only if it's not cached
                if (string.IsNullOrEmpty(me)) me = client.User.Current().GetAwaiter().GetResult().Login;
                bool mine = string.Equals(who.User, me, StringComparison.OrdinalIgnoreCase);
                bool owner = ship.Owner != null && string.Equals(ship.Owner, me, StringComparison.OrdinalIgnoreCase);
                if (!mine && !owner)
                    throw new Exception("Checked out by @" + who.User + " - only they (or the ship's owner) can release it.");
                client.Git.Reference.Delete(Auth.RepoOwner, Auth.RepoName, "heads/" + CheckoutBranch(cs)).GetAwaiter().GetResult();
                _checkoutBase.Remove(cs);
                Plugin.Log("released checkout " + cs);
                return "Released the checkout of '" + cs + "' (work branch deleted, lock cleared).";
            });
        }

        // ship path -> who, for every checkout/ branch (one refs call + a tiny commit read each).
        private static Dictionary<string, string> FetchCheckouts(GitHubClient client)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var refs = client.Git.Reference.GetAllForSubNamespace(Auth.RepoOwner, Auth.RepoName, "heads/checkout").GetAwaiter().GetResult();
                foreach (var r in refs)
                {
                    if (r.Ref == null || !r.Ref.StartsWith("refs/heads/checkout/")) continue;
                    string cs = r.Ref.Substring("refs/heads/checkout/".Length);
                    string user = "?";
                    try
                    {
                        var c = client.Git.Commit.Get(Auth.RepoOwner, Auth.RepoName, r.Object.Sha).GetAwaiter().GetResult();
                        var m = Regex.Match(c.Message ?? "", @"by @(\S+)");
                        if (m.Success) user = m.Groups[1].Value;
                    }
                    catch (Exception ex) { Plugin.Log("checkout who failed " + cs + ": " + ex.Message); }
                    map[cs] = user;
                }
            }
            catch (NotFoundException) { /* no checkouts yet */ }
            catch (Exception ex) { Plugin.Log("checkout list failed: " + ex.Message); }
            return map;
        }

        // Pull EVERY ship on main into the local library in one pass (a full sync / "checkout all").
        public static void InstallAll()
        {
            if (Auth.IsOffline)
            {
                ShipyardRunner.RunWithBusy("Installing all local ships...", () =>
                {
                    var ships = LocalData().Ships;
                    int n = 0;
                    foreach (var s in ships)
                        try { string dn; LocalInstall(s.CategoryShip, out dn); n++; }
                        catch (Exception ex) { Plugin.Log("local install " + s.CategoryShip + ": " + ex.Message); }
                    return n == 0 ? "No local ships to install yet." : "Installed " + n + " ship(s) into your blueprint library.";
                });
                return;
            }
            ShipyardRunner.RunWithBusy("Pulling all ships from main...", () =>
            {
                var client = Gh();
                var tree = client.Git.Tree.GetRecursive(Auth.RepoOwner, Auth.RepoName, "main").GetAwaiter().GetResult();
                var owners = ReadCodeowners(client);
                var bps = tree.Tree.Where(x => x.Type == TreeType.Blob && x.Path.StartsWith(RootSlash) && x.Path.EndsWith("/bp.sbc")).ToList();
                if (bps.Count == 0) return "No ships on main yet.";

                int ships = 0, files = 0;
                foreach (var bp in bps)
                {
                    string cs = bp.Path.Substring(RootSlash.Length, bp.Path.Length - RootSlash.Length - 7); // "PvP/Red-Ship"
                    string destName;
                    files += InstallFromTree(client, tree, cs, owners, out destName);
                    ships++;
                }
                Plugin.Log("pull-all: " + ships + " ships, " + files + " files");
                return "Pulled " + ships + " ships (" + files + " files) from main into your library.\n" +
                       "They appear under Blueprints (F10) after a refresh/reload.";
            });
        }

        // Load a repo ship straight into the projector the player is looking at (primary grid only).
        public static void ProjectByQuery(string query)
        {
            if (MyAPIGateway.Session == null) { ShipyardRunner.ShowMessage("Projecting requires being in a world."); return; }
            IMyProjector proj;
            if (!TryGetLookedAtProjector(out proj)) { ShipyardRunner.ShowMessage("Look at a projector first, then run /sy project <ship>."); return; }
            ResolveShipThen(query, s => LoadIntoProjector(s.CategoryShip, proj));
        }

        private static bool TryGetLookedAtProjector(out IMyProjector proj)
        {
            proj = null;
            try
            {
                var cam = MyAPIGateway.Session.Camera;
                IHitInfo hit;
                if (!MyAPIGateway.Physics.CastRay(cam.Position, cam.Position + cam.WorldMatrix.Forward * ProjectorRayMeters, out hit) || hit == null) return false;
                var grid = hit.HitEntity as IMyCubeGrid;
                if (grid == null) return false;
                var blocks = new List<IMySlimBlock>();
                grid.GetBlocks(blocks, b => b.FatBlock is IMyProjector);
                double best = double.MaxValue;
                foreach (var b in blocks)
                {
                    double d = Vector3D.DistanceSquared(b.FatBlock.GetPosition(), hit.Position);
                    if (d < best) { best = d; proj = b.FatBlock as IMyProjector; }
                }
            }
            catch (Exception ex) { Plugin.Log("projector raycast failed: " + ex.Message); }
            return proj != null;
        }

        private static void LoadIntoProjector(string categoryShip, IMyProjector proj)
        {
            ShipyardRunner.RunWithBusyThen<MyObjectBuilder_CubeGrid[]>(
                "Loading " + categoryShip + " into projector...",
                () => Auth.IsOffline ? LocalGrids(categoryShip) : FetchGrids(categoryShip),
                grids =>
                {
                    try
                    {
                        if (proj == null || proj.Closed) { ShipyardRunner.ShowMessage("That projector is gone."); return; }
                        proj.SetProjectedGrid(grids[0]);
                        ShipyardRunner.ShowResult("Projected '" + categoryShip + "' onto " + (proj.CustomName ?? "the projector") + "." +
                            (grids.Length > 1 ? "\n(multi-grid blueprint: only the primary grid projects)" : ""));
                    }
                    catch (Exception ex) { Plugin.Log("SetProjectedGrid failed: " + ex); ShipyardRunner.ShowMessage("Projector load failed: " + ex.Message); }
                });
        }

        // Marker file so we (and a future F10 badge) can tell which local blueprints came from the repo.
        private static void WriteMeta(string dest, string categoryShip, string owner)
        {
            try
            {
                string cat = categoryShip.Contains("/") ? categoryShip.Substring(0, categoryShip.IndexOf('/')) : "";
                File.WriteAllText(Path.Combine(dest, "shipyard.meta"),
                    "category: " + cat + "\n" +
                    "path: " + RootSlash + categoryShip + "\n" +
                    "repo: " + Auth.RepoOwner + "/" + Auth.RepoName + "\n" +
                    "owner: " + (owner ?? "") + "\n");
            }
            catch (Exception ex) { Plugin.Log("WriteMeta failed: " + ex.Message); }
        }

        private static Dictionary<string, string> ReadCodeowners(GitHubClient client)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var co = client.Repository.Content.GetAllContents(Auth.RepoOwner, Auth.RepoName, ".github/CODEOWNERS").GetAwaiter().GetResult();
                string text = co.Count > 0 ? (co[0].Content ?? "") : "";
                foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                {
                    var m = Regex.Match(line.Trim(), @"^/(\S+)/\s+@(\S+)");
                    if (m.Success) map[m.Groups[1].Value] = m.Groups[2].Value; // rel -> owner
                }
            }
            catch (Exception ex) { Plugin.Log("read CODEOWNERS failed: " + ex.Message); }
            return map;
        }

        // Tree at an arbitrary COMMIT (GetRefTree only resolves branch heads).
        private static TreeResponse GetCommitTree(GitHubClient client, string commitSha)
        {
            try
            {
                var commit = client.Git.Commit.Get(Auth.RepoOwner, Auth.RepoName, commitSha).GetAwaiter().GetResult();
                return client.Git.Tree.GetRecursive(Auth.RepoOwner, Auth.RepoName, commit.Tree.Sha).GetAwaiter().GetResult();
            }
            catch (Exception ex) { Plugin.Log("GetCommitTree(" + commitSha + ") failed: " + ex.Message); return null; }
        }

        // The version a PR should be DIFFED against: the ship on main or, if it's gone from main
        // (deleted while the PR was open), the version at the PR's base commit, so the review still
        // shows the author's actual changes instead of an all-"added" wall. fromBase reports which.
        private static byte[] PrBaselineBytes(GitHubClient client, PrEntry pr, out bool fromBase)
        {
            fromBase = false;
            byte[] bytes = GetBlueprintBytes(client, "main", pr.CategoryShip);
            if (bytes != null || string.IsNullOrEmpty(pr.BaseSha)) return bytes;
            var tree = GetCommitTree(client, pr.BaseSha);
            bytes = tree == null ? null : TreeFileBytes(client, tree, RootSlash + pr.CategoryShip + "/bp.sbc");
            fromBase = bytes != null;
            return bytes;
        }

        private static List<PrEntry> FetchPullRequests(Dictionary<string, string> owners = null)
        {
            var client = Gh();
            string me = Auth.Login;   // we store the login at sign-in; no need for a User.Current round-trip
            if (string.IsNullOrEmpty(me)) { try { me = client.User.Current().GetAwaiter().GetResult().Login; } catch (Exception ex) { Plugin.Log("login lookup (User.Current) failed: " + ex.Message); } }
            if (owners == null) owners = ReadCodeowners(client);
            var prs = client.PullRequest.GetAllForRepository(Auth.RepoOwner, Auth.RepoName).GetAwaiter().GetResult();

            var result = new List<PrEntry>();
            foreach (var pr in prs)
            {
                var e = new PrEntry { Number = pr.Number, Author = pr.User.Login, Title = pr.Title };
                string branch = pr.Head != null ? pr.Head.Ref : null;
                e.HeadBranch = branch;
                e.BaseSha = pr.Base != null ? pr.Base.Sha : null;
                // Both publish (ship/...) and checkout (checkout/...) branches map to a ship path.
                if (!string.IsNullOrEmpty(branch))
                {
                    if (branch.StartsWith("ship/")) e.CategoryShip = branch.Substring("ship/".Length);
                    else if (branch.StartsWith("checkout/")) e.CategoryShip = branch.Substring("checkout/".Length);
                    if (e.CategoryShip != null)
                    {
                        string rel = RootSlash + e.CategoryShip;
                        if (owners.ContainsKey(rel)) e.Owner = owners[rel];
                    }
                }
                e.IsMine = string.Equals(pr.User.Login, me, StringComparison.OrdinalIgnoreCase);
                e.NeedsMyReview = e.Owner != null && string.Equals(e.Owner, me, StringComparison.OrdinalIgnoreCase) && !e.IsMine;
                e.HtmlUrl = pr.HtmlUrl;
                result.Add(e);
            }

            // Per-PR review + mergeable + (first-time) thumb: independent, so run all PRs CONCURRENTLY
            // instead of 2+ sequential round-trips each.
            ParallelForEach(result, e =>
            {
                try
                {
                    var reviews = client.PullRequest.Review.GetAll(Auth.RepoOwner, Auth.RepoName, e.Number).GetAwaiter().GetResult();
                    e.OwnerApproved = e.Owner != null && reviews.Any(r =>
                        string.Equals(r.User.Login, e.Owner, StringComparison.OrdinalIgnoreCase) &&
                        r.State.Value == PullRequestReviewState.Approved);
                }
                catch (Exception ex) { Plugin.Log("reviews fetch failed PR#" + e.Number + ": " + ex.Message); }

                try { e.Mergeable = client.PullRequest.Get(Auth.RepoOwner, Auth.RepoName, e.Number).GetAwaiter().GetResult().Mergeable; }
                catch (Exception ex) { Plugin.Log("mergeable fetch failed PR#" + e.Number + ": " + ex.Message); }

                if (e.CategoryShip != null)
                {
                    // Use the cached main thumb if present, else pull the PR branch's thumb so the tile has an image.
                    string tp = Path.Combine(CacheDir(), e.CategoryShip.Replace('/', '_'), "thumb.png");
                    if (!File.Exists(tp))
                    {
                        try
                        {
                            byte[] thumb = GetBlueprintFileBytes(client, e.HeadBranch, e.CategoryShip, "thumb.png");
                            if (thumb != null)
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(tp));
                                File.WriteAllBytes(tp, NormalizeThumb(thumb));
                            }
                        }
                        catch (Exception ex) { Plugin.Log("PR thumb fetch failed #" + e.Number + ": " + ex.Message); }
                    }
                    if (File.Exists(tp)) e.ThumbPath = tp;
                }
            });
            // Group order: your review queue first, then your own submissions, then everything else.
            int Rank(PrEntry e) => e.NeedsMyReview ? 0 : e.IsMine ? 1 : 2;
            result = result.OrderBy(Rank).ThenByDescending(e => e.Number).ToList();
            Plugin.Log("fetched " + result.Count + " open PRs");
            return result;
        }

        // Resolve a branch name ("main", "checkout/PvP/Foo", "ship/PvP/Bar") to its HEAD commit sha, or
        // null if no such branch exists. Why not the obvious single-ref calls:
        //   * Git.Reference.Get("heads/"+ref) hits GitHub's ref-MATCHING endpoint, which prefix-matches
        //     and returns a JSON ARRAY when the requested ref is a prefix of another ref (e.g. two ships
        //     "Foo" and "Foo-Mk2" both checked out). Octokit forces that array into a single Reference
        //     with a null .Object, so .Object.Sha throws an NRE.
        //   * Repository.Commit.Get(ref) URL-encodes the slashes, so GitHub never resolves it as a branch
        //     and reports "No commit found for SHA: checkout/PvP/...".
        // The matching-refs LIST endpoint sidesteps both: it returns a real list (empty when nothing
        // matches), and we pick the EXACT ref ourselves. Slashes are fine; prefix collisions are fine.
        private static string ResolveBranchSha(GitHubClient client, string branch)
        {
            string want = "refs/heads/" + branch;
            var all = client.Git.Reference.GetAllForSubNamespace(Auth.RepoOwner, Auth.RepoName, "heads/" + branch).GetAwaiter().GetResult();
            var exact = all.FirstOrDefault(r => string.Equals(r.Ref, want, StringComparison.Ordinal));
            return exact?.Object?.Sha;
        }

        // Resolve a branch ref to its full recursive tree, or null (no such branch / error).
        private static TreeResponse GetRefTree(GitHubClient client, string gitRef)
        {
            try
            {
                string sha = ResolveBranchSha(client, gitRef);
                if (sha == null) { Plugin.Log("GetRefTree(" + gitRef + "): no such branch"); return null; }
                var commit = client.Git.Commit.Get(Auth.RepoOwner, Auth.RepoName, sha).GetAwaiter().GetResult();
                return client.Git.Tree.GetRecursive(Auth.RepoOwner, Auth.RepoName, commit.Tree.Sha).GetAwaiter().GetResult();
            }
            catch (Exception ex) { Plugin.Log("GetRefTree(" + gitRef + ") failed: " + ex.Message); return null; }
        }

        // One file's bytes out of an already-fetched tree, or null if absent.
        private static byte[] TreeFileBytes(GitHubClient client, TreeResponse tree, string path)
        {
            try
            {
                var e = tree.Tree.FirstOrDefault(x => x.Type == TreeType.Blob && x.Path == path);
                if (e == null) return null;
                return CachedBlobBytes(client, e.Sha);
            }
            catch (Exception ex) { Plugin.Log("TreeFileBytes(" + path + ") failed: " + ex.Message); return null; }
        }

        // Resolve a ref ("main" or "ship/PvP/Foo") to its bp.sbc bytes for a given ship, or null.
        private static byte[] GetBlueprintBytes(GitHubClient client, string gitRef, string categoryShip)
            => GetBlueprintFileBytes(client, gitRef, categoryShip, "bp.sbc");

        // bp.sbc bytes -> map of "x,y,z" -> block (all grids flattened).
        private static Dictionary<string, MyObjectBuilder_CubeBlock> BlocksByPosition(byte[] sbc)
        {
            var map = new Dictionary<string, MyObjectBuilder_CubeBlock>();
            MyObjectBuilder_Definitions defs;
            ulong sz;
            if (!MyObjectBuilderSerializer.DeserializeXML(sbc, out defs, out sz)) return map;
            if (defs.ShipBlueprints == null) return map;
            foreach (var bp in defs.ShipBlueprints)
                if (bp.CubeGrids != null)
                    foreach (var g in bp.CubeGrids)
                        if (g.CubeBlocks != null)
                            foreach (var b in g.CubeBlocks)
                            {
                                string key = b.Min.X + "," + b.Min.Y + "," + b.Min.Z;
                                map[key] = b; // last wins (multi-grid position collisions are rare)
                            }
            return map;
        }

        // bp.sbc bytes -> block map of the PRIMARY grid only. The diff compares one grid against one grid:
        // flattening subgrids (rotor/hinge weapon mounts, etc.) into a single Min-keyed space mixes
        // independent pivot frames, so a subgrid block lands far from the hull and reads as a phantom
        // add/remove. Diffs use this; count/summary displays still use the all-grids BlocksByPosition.
        private static Dictionary<string, MyObjectBuilder_CubeBlock> BlocksByPositionPrimary(byte[] sbc)
        {
            var map = new Dictionary<string, MyObjectBuilder_CubeBlock>();
            MyObjectBuilder_Definitions defs; ulong sz;
            if (!MyObjectBuilderSerializer.DeserializeXML(sbc, out defs, out sz)) return map;
            if (defs.ShipBlueprints == null || defs.ShipBlueprints.Length == 0) return map;
            var grids = defs.ShipBlueprints[0].CubeGrids;
            if (grids == null || grids.Length == 0 || grids[0].CubeBlocks == null) return map;
            foreach (var b in grids[0].CubeBlocks) map[b.Min.X + "," + b.Min.Y + "," + b.Min.Z] = b;
            return map;
        }

        // In-memory primary grid -> block map (offline/commit path, no XML round-trip).
        private static Dictionary<string, MyObjectBuilder_CubeBlock> BlocksFromPrimaryGrid(MyObjectBuilder_CubeGrid[] grids)
        {
            var map = new Dictionary<string, MyObjectBuilder_CubeBlock>();
            if (grids == null || grids.Length == 0 || grids[0].CubeBlocks == null) return map;
            foreach (var b in grids[0].CubeBlocks) map[b.Min.X + "," + b.Min.Y + "," + b.Min.Z] = b;
            return map;
        }

        // --- pivot-shift recovery --------------------------------------------------------------
        // A grid's block Min cells are RELATIVE to its pivot/origin. If the pivot shifts between two
        // versions of a ship (a re-origin on rebuild/paste), every Min moves by the SAME vector, so a
        // naive position-keyed diff reports ~100% changed - and the visual diff then spawns a box per
        // block (lag). A pivot shift is a pure TRANSLATION, hence recoverable: find the offset that best
        // re-aligns the two block sets, diff in that frame, and only the genuine edits remain.
        private struct PosSub { public Vector3I Pos; public string Sub; }

        private static List<PosSub> PosSubs(Dictionary<string, MyObjectBuilder_CubeBlock> d)
        {
            var l = new List<PosSub>(d.Count);
            foreach (var b in d.Values) { Vector3I p = b.Min; l.Add(new PosSub { Pos = p, Sub = b.SubtypeName }); }
            return l;
        }

        private static Vector3I CornerMin(List<PosSub> l)
        {
            int mx = int.MaxValue, my = int.MaxValue, mz = int.MaxValue;
            foreach (var p in l) { if (p.Pos.X < mx) mx = p.Pos.X; if (p.Pos.Y < my) my = p.Pos.Y; if (p.Pos.Z < mz) mz = p.Pos.Z; }
            return new Vector3I(mx, my, mz);
        }

        private static Vector3I CentroidOf(List<PosSub> l)
        {
            long sx = 0, sy = 0, sz = 0; int n = Math.Max(1, l.Count);
            foreach (var p in l) { sx += p.Pos.X; sy += p.Pos.Y; sz += p.Pos.Z; }
            return new Vector3I((int)Math.Round((double)sx / n), (int)Math.Round((double)sy / n), (int)Math.Round((double)sz / n));
        }

        // The translation to ADD to 'from' positions so they best line up with 'to' (recovers a shifted
        // pivot). 'matches' = blocks that coincide (same cell + same subtype) under it. Returns zero when
        // the grids are already aligned or no better alignment is found
        private static Vector3I RecoverOffset(List<PosSub> from, List<PosSub> to, out int matches)
        {
            matches = 0;
            if (from.Count == 0 || to.Count == 0) return Vector3I.Zero;

            var toMap = new Dictionary<Vector3I, string>(to.Count);
            var toBySub = new Dictionary<string, List<Vector3I>>();
            foreach (var t in to)
            {
                toMap[t.Pos] = t.Sub ?? "";
                List<Vector3I> bl; if (!toBySub.TryGetValue(t.Sub ?? "", out bl)) { bl = new List<Vector3I>(); toBySub[t.Sub ?? ""] = bl; }
                bl.Add(t.Pos);
            }

            // Cheap candidates first (exact for a pure pivot shift): identity, bounding-box-min and
            // centroid alignment. Then modal votes from RARE subtypes only (short 'to' lists -> cheap +
            // unambiguous signal; avoids the vote explosion of an all-armor hull).
            var cands = new HashSet<Vector3I> { Vector3I.Zero, CornerMin(to) - CornerMin(from), CentroidOf(to) - CentroidOf(from) };
            var votes = new Dictionary<Vector3I, int>();
            foreach (var f in from)
            {
                List<Vector3I> tl;
                if (!toBySub.TryGetValue(f.Sub ?? "", out tl) || tl.Count > RecoverVoteCap) continue;
                foreach (var tp in tl) { var off = tp - f.Pos; votes[off] = votes.TryGetValue(off, out var c) ? c + 1 : 1; }
            }
            if (votes.Count > 0)
            {
                Vector3I top = Vector3I.Zero; int tv = -1;
                foreach (var kv in votes) if (kv.Value > tv) { tv = kv.Value; top = kv.Key; }
                cands.Add(top);
            }

            Vector3I best = Vector3I.Zero; int bestM = -1;
            foreach (var off in cands)
            {
                int m = 0;
                foreach (var f in from) { string s; if (toMap.TryGetValue(f.Pos + off, out s) && s == (f.Sub ?? "")) m++; }
                if (m > bestM || (m == bestM && off == Vector3I.Zero)) { bestM = m; best = off; }
            }
            matches = bestM;
            return best;
        }

        // main's block dict re-keyed into the target frame
        private static Dictionary<string, MyObjectBuilder_CubeBlock> ShiftKeys(Dictionary<string, MyObjectBuilder_CubeBlock> d, Vector3I off)
        {
            if (off.X == 0 && off.Y == 0 && off.Z == 0) return d;
            var r = new Dictionary<string, MyObjectBuilder_CubeBlock>(d.Count);
            foreach (var b in d.Values) { Vector3I m = b.Min; r[(m.X + off.X) + "," + (m.Y + off.Y) + "," + (m.Z + off.Z)] = b; }
            return r;
        }

        // >80% of blocks differing even after alignment = a different grid (or an unrecoverable pivot).
        private static bool LooksLikeDifferentGrid(int matches, int countA, int countB)
        {
            int refCount = Math.Min(countA, countB);
            return countA > 0 && refCount > 0 && matches < DifferentGridRatio * refCount;
        }

        // Open a readable details view for a PR: ship/folder/author/owner, new-vs-update, and a block
        // change summary. (The tile carries the screenshot; this is the textual breakdown.)
        public static void ShowPrDetails(PrEntry pr)
        {
            // Modal busy: details are a "clicked and waiting" action. The answer should land in
            // front of the player, not pop up later over whatever they moved on to.
            ShipyardRunner.RunWithBusyModalThen<string>(
                "Loading PR #" + pr.Number + " details...",
                () =>
                {
                    var client = Gh();
                    byte[] prBytes = GetBlueprintBytes(client, pr.HeadBranch, pr.CategoryShip);
                    bool baseFallback;
                    byte[] mainBytes = PrBaselineBytes(client, pr, out baseFallback);
                    string cs = pr.CategoryShip ?? "?";
                    int sl = cs.LastIndexOf('/');
                    string name = sl > 0 ? cs.Substring(sl + 1) : cs;
                    string folder = sl > 0 ? cs.Substring(0, sl) : "(root)";

                    var sb = new StringBuilder();
                    sb.AppendLine("Ship:     " + name);
                    sb.AppendLine("Folder:   " + RootSlash + folder);
                    sb.AppendLine("Author:   @" + pr.Author);
                    sb.AppendLine("Owner:    " + (pr.Owner != null ? "@" + pr.Owner : "(none recorded yet)"));
                    sb.AppendLine("PR:       #" + pr.Number + "   status: " + pr.Status());
                    sb.AppendLine("Type:     " + (mainBytes == null ? "NEW ship (not yet on main)"
                                  : baseFallback ? "UPDATE - but the ship was DELETED from main (diff vs the checked-out version)"
                                  : "UPDATE to an existing ship"));
                    sb.AppendLine("");
                    if (prBytes == null) sb.AppendLine("(couldn't read the PR blueprint)");
                    else if (mainBytes == null) sb.AppendLine("Blocks:   " + BlocksByPosition(prBytes).Count + "  (all new)");
                    else
                    {
                        var prB = BlocksByPosition(prBytes); var mB = BlocksByPosition(mainBytes);
                        int matches; var off = RecoverOffset(PosSubs(mB), PosSubs(prB), out matches);
                        var mShift = ShiftKeys(mB, off);
                        int added = prB.Keys.Count(k => !mShift.ContainsKey(k));
                        int removed = mShift.Keys.Count(k => !prB.ContainsKey(k));
                        int replaced = prB.Keys.Count(k => mShift.ContainsKey(k) &&
                            !string.Equals(prB[k].SubtypeName, mShift[k].SubtypeName, StringComparison.Ordinal));
                        sb.AppendLine("Blocks:   " + mB.Count + "  ->  " + prB.Count);
                        if (LooksLikeDifferentGrid(matches, mB.Count, prB.Count))
                            sb.AppendLine("Changes:  >80% differ - looks like a DIFFERENT grid (or an unrecoverable pivot).");
                        else
                        {
                            if (off != Vector3I.Zero) sb.AppendLine("Pivot:    shifted by (" + off.X + "," + off.Y + "," + off.Z + ") - realigned before diffing.");
                            sb.AppendLine("Changes:  +" + added + " added    -" + removed + " removed    ~" + replaced + " replaced");

                            // Settings/loadout changes (same block, different custom data / mod
                            // storage e.g. inventory-sorter configs): list which blocks, so a
                            // loadout-only PR isn't invisible in review.
                            var dataChanged = prB.Keys.Where(k => mShift.ContainsKey(k) &&
                                string.Equals(prB[k].SubtypeName, mShift[k].SubtypeName, StringComparison.Ordinal) &&
                                SettingsDiffer(mShift[k], prB[k])).ToList();
                            if (dataChanged.Count > 0)
                            {
                                sb.AppendLine("Data:     ±" + dataChanged.Count + " block" + (dataChanged.Count == 1 ? "" : "s") + " with changed custom data:");
                                foreach (var k in dataChanged.Take(12))
                                {
                                    var nb = prB[k];
                                    string nm = ObBlockName(nb);
                                    sb.AppendLine("            ~ " + (nm ?? nb.SubtypeName) + "   (" + nb.SubtypeName + ")");
                                }
                                if (dataChanged.Count > 12) sb.AppendLine("            ... and " + (dataChanged.Count - 12) + " more");
                                sb.AppendLine("          (Visual Diff marks them MAGENTA - aim + Ctrl+Shift+D for the line diff.)");
                            }
                        }
                    }
                    sb.AppendLine("");
                    sb.AppendLine("Use 'Visual Diff' to see the changes in-world,");
                    sb.AppendLine("or open the full diff on GitHub:");
                    sb.AppendLine(pr.HtmlUrl ?? "(n/a)");
                    return sb.ToString();
                },
                text => MyGuiSandbox.AddScreen(new InfoScreen("PR #" + pr.Number + "  -  " + (pr.CategoryShip ?? ""), text)));
        }

        // Specs for a repo ship: the metadata we track (path/owner/tags) plus stats parsed from the
        // blueprint on main (grids, block count, grid size, bounding-box dimensions).
        public static void ShowShipDetails(ShipEntry e)
        {
            if (e == null) { ShipyardRunner.ShowMessage("Select a ship first."); return; }
            // Modal busy (see ShowPrDetails) - specs are requested-and-awaited, not background info.
            ShipyardRunner.RunWithBusyModalThen<string>(
                "Loading " + e.Name + " specs...",
                () =>
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("Ship:     " + e.Name);
                    sb.AppendLine("Folder:   " + RootSlash + (string.IsNullOrEmpty(e.Folder) ? "" : e.Folder));
                    sb.AppendLine("Owner:    " + (e.Owner != null ? "@" + e.Owner : "(none recorded)"));
                    sb.AppendLine("Status:   " + (e.CheckedOutBy != null ? "CHECKED OUT by @" + e.CheckedOutBy : "available"));
                    sb.AppendLine("Tags:     " + (e.Tags != null && e.Tags.Count > 0 ? string.Join(", ", e.Tags) : "(none)"));
                    sb.AppendLine("");

                    byte[] bytes = null;
                    try { bytes = RepoOrLocalBytes(e.CategoryShip); }
                    catch (Exception ex) { Plugin.Log("ship details fetch failed: " + ex.Message); }

                    MyObjectBuilder_Definitions defs; ulong sz;
                    if (bytes == null || !MyObjectBuilderSerializer.DeserializeXML(bytes, out defs, out sz) ||
                        defs.ShipBlueprints == null || defs.ShipBlueprints.Length == 0)
                    {
                        sb.AppendLine("(couldn't read the blueprint specs)");
                        return sb.ToString();
                    }

                    var grids = defs.ShipBlueprints[0].CubeGrids ?? new MyObjectBuilder_CubeGrid[0];
                    int totalBlocks = 0;
                    int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
                    int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
                    string size = null;
                    foreach (var g in grids)
                    {
                        if (size == null) size = g.GridSizeEnum.ToString();
                        if (g.CubeBlocks == null) continue;
                        totalBlocks += g.CubeBlocks.Count;
                        foreach (var b in g.CubeBlocks)
                        {
                            if (b.Min.X < minX) minX = b.Min.X; if (b.Min.Y < minY) minY = b.Min.Y; if (b.Min.Z < minZ) minZ = b.Min.Z;
                            if (b.Min.X > maxX) maxX = b.Min.X; if (b.Min.Y > maxY) maxY = b.Min.Y; if (b.Min.Z > maxZ) maxZ = b.Min.Z;
                        }
                    }
                    sb.AppendLine("Grids:    " + grids.Length);
                    sb.AppendLine("Blocks:   " + totalBlocks);
                    if (size != null) sb.AppendLine("Grid:     " + size);
                    if (maxX >= minX)
                    {
                        int dx = maxX - minX + 1, dy = maxY - minY + 1, dz = maxZ - minZ + 1;
                        float m = string.Equals(size, "Large", StringComparison.OrdinalIgnoreCase) ? 2.5f : 0.5f;
                        sb.AppendLine("Size:     " + dx + " x " + dy + " x " + dz + " blocks   (" +
                            (dx * m).ToString("0.#") + " x " + (dy * m).ToString("0.#") + " x " + (dz * m).ToString("0.#") + " m)");
                    }
                    AppendDefinitionStats(sb, grids, totalBlocks);
                    return sb.ToString();
                },
                text => MyGuiSandbox.AddScreen(new InfoScreen(e.CategoryShip ?? "Ship", text)));
        }

        // Mass / power / thrust / acceleration computed from the LOADED block definitions, so values
        // changed by installed mods are reflected automatically. Blocks whose definition isn't
        // installed locally (missing mod/DLC) are skipped and reported, so the totals stay honest.
        private static void AppendDefinitionStats(StringBuilder sb, MyObjectBuilder_CubeGrid[] grids, int totalBlocks)
        {
            try
            {
                double massKg = 0, powerMw = 0, batteryMw = 0;
                var thrustN = new Dictionary<Vector3I, double>();   // grid-space push direction -> Newtons
                int unknown = 0;
                foreach (var g in grids)
                {
                    if (g.CubeBlocks == null) continue;
                    foreach (var b in g.CubeBlocks)
                    {
                        MyCubeBlockDefinition def = null;
                        try { def = MyDefinitionManager.Static.GetCubeBlockDefinition(new MyDefinitionId(b.TypeId, b.SubtypeId)); }
                        catch { }   // unknown/modded block id -> def stays null and is counted as 'unknown' below
                        if (def == null) { unknown++; continue; }
                        massKg += def.Mass;
                        if (def is MyThrustDefinition th)
                        {
                            // a thruster PUSHES the grid opposite its Forward (MyThrust.ThrustForce = -Forward * F)
                            var dir = -Base6Directions.GetIntVector(b.BlockOrientation.Forward);
                            double f; thrustN.TryGetValue(dir, out f);
                            thrustN[dir] = f + th.ForceMagnitude;
                        }
                        else if (def is MyBatteryBlockDefinition bat) batteryMw += bat.MaxPowerOutput;
                        else if (def is MyPowerProducerDefinition pp) powerMw += pp.MaxPowerOutput;   // reactor/solar/wind/H2
                    }
                }

                if (unknown >= totalBlocks && totalBlocks > 0)
                { sb.AppendLine(""); sb.AppendLine("(mass/power/thrust need a loaded world's definitions)"); return; }

                sb.AppendLine("");
                sb.AppendLine("Mass:     " + FmtMass(massKg) + "   (dry - blocks only, no cargo)");
                sb.AppendLine("Power:    " + FmtMw(powerMw) + " generated" +
                    (batteryMw > 0 ? "   +" + FmtMw(batteryMw) + " battery discharge" : ""));
                if (thrustN.Count > 0 && massKg > 0)
                {
                    double best = thrustN.Values.Max(), worst = thrustN.Values.Min();
                    sb.AppendLine("Thrust:   " + FmtForce(best) + " best axis" +
                        (thrustN.Count > 1 ? "   /   " + FmtForce(worst) + " weakest" : ""));
                    sb.AppendLine("Accel:    " + (best / massKg).ToString("0.0#") + " m/s2 best" +
                        (thrustN.Count > 1 ? "   /   " + (worst / massKg).ToString("0.0#") + " m/s2 weakest" : "") + "   (at dry mass)");
                }
                if (unknown > 0)
                    sb.AppendLine("(" + unknown + " block" + (unknown == 1 ? "" : "s") + " skipped - mod/DLC not installed here)");
            }
            catch (Exception ex) { Plugin.Log("definition stats failed: " + ex.Message); }
        }

        private static string FmtMw(double mw) =>
            mw >= 1000 ? (mw / 1000).ToString("0.##") + " GW" : mw.ToString("0.##") + " MW";
        private static string FmtMass(double kg) =>
            kg >= 1e6 ? (kg / 1e6).ToString("0.##") + " kt" :
            kg >= 1000 ? (kg / 1000).ToString("0.#") + " t" : kg.ToString("0") + " kg";
        private static string FmtForce(double n) =>
            n >= 1e6 ? (n / 1e6).ToString("0.##") + " MN" : (n / 1000).ToString("0.#") + " kN";

        // ----- visual overlay diff) -----
        // green=added, red=removed, cyan=repainted.
        private static bool ColorDiffers(SerializableVector3 a, SerializableVector3 b) =>
            Math.Abs(a.X - b.X) > 0.001f || Math.Abs(a.Y - b.Y) > 0.001f || Math.Abs(a.Z - b.Z) > 0.001f;
        private static bool ColorDiffers(Vector3 a, SerializableVector3 b) =>
            Math.Abs(a.X - b.X) > 0.001f || Math.Abs(a.Y - b.Y) > 0.001f || Math.Abs(a.Z - b.Z) > 0.001f;
        private static string PosKey(SerializableVector3I v) => v.X + "," + v.Y + "," + v.Z;
        // A highlight box spanning a block's full cell extent [min..max], centred on the block.
        // Category (added/removed/changed/recolored/data) drives both the user filter and the draw color.
        private static HighlightManager.Box BoxFor(Vector3I min, Vector3I max, double cell, string category, string label)
        {
            var center = new Vector3D(min.X + max.X, min.Y + max.Y, min.Z + max.Z) * 0.5 * cell;
            var size = new Vector3D(max.X - min.X + 1, max.Y - min.Y + 1, max.Z - min.Z + 1) * cell * 0.96;
            return new HighlightManager.Box { Center = center, Size = size, Category = category, Label = label };
        }

        // Cell extent of an object-builder block: Min from the OB, Max from the definition Size
        // rotated by the block's orientation (so multi-cell blocks get a full-footprint box).
        private static void ObExtent(MyObjectBuilder_CubeBlock b, out Vector3I min, out Vector3I max)
        {
            min = b.Min; max = b.Min;
            try
            {
                var def = MyDefinitionManager.Static.GetCubeBlockDefinition(new MyDefinitionId(b.TypeId, b.SubtypeId));
                if (def == null) return;
                Matrix m;
                new MyBlockOrientation(b.BlockOrientation.Forward, b.BlockOrientation.Up).GetMatrix(out m);
                Vector3 s = Vector3.TransformNormal((Vector3)def.Size, m);
                var os = new Vector3I((int)Math.Round(Math.Abs(s.X)), (int)Math.Round(Math.Abs(s.Y)), (int)Math.Round(Math.Abs(s.Z)));
                max = min + os - Vector3I.One;
            }
            catch (Exception ex) { Plugin.Log("ObExtent failed: " + ex.Message); }
        }

        private static bool BoxesOverlap(HighlightManager.Box a, HighlightManager.Box b)
        {
            Vector3D amin = a.Center - a.Size * 0.5, amax = a.Center + a.Size * 0.5;
            Vector3D bmin = b.Center - b.Size * 0.5, bmax = b.Center + b.Size * 0.5;
            return amin.X <= bmax.X && amax.X >= bmin.X && amin.Y <= bmax.Y && amax.Y >= bmin.Y && amin.Z <= bmax.Z && amax.Z >= bmin.Z;
        }

        private static List<HighlightManager.Box> CollapseReplacements(List<HighlightManager.Box> boxes)
        {
            var reds = boxes.Where(b => b.Category == "removed").ToList();
            var greens = boxes.Where(b => b.Category == "added").ToList();
            var result = boxes.Where(b => b.Category != "removed" && b.Category != "added").ToList();
            var usedGreen = new bool[greens.Count];
            foreach (var r in reds)
            {
                int gi = -1;
                for (int i = 0; i < greens.Count; i++)
                    if (!usedGreen[i] && BoxesOverlap(r, greens[i])) { gi = i; break; }
                if (gi < 0) { result.Add(r); continue; }
                usedGreen[gi] = true;
                var g = greens[gi];
                Vector3D min = Vector3D.Min(r.Center - r.Size * 0.5, g.Center - g.Size * 0.5);
                Vector3D max = Vector3D.Max(r.Center + r.Size * 0.5, g.Center + g.Size * 0.5);
                string rs = r.Label != null && r.Label.Length > 2 ? r.Label.Substring(2) : r.Label;
                string gs = g.Label != null && g.Label.Length > 2 ? g.Label.Substring(2) : g.Label;
                result.Add(new HighlightManager.Box { Center = (min + max) * 0.5, Size = (max - min), Category = "changed", Label = "~ " + rs + "  ->  " + gs });
            }
            for (int i = 0; i < greens.Count; i++) if (!usedGreen[i]) result.Add(greens[i]);
            return result;
        }

        class DiffPayload { public MyObjectBuilder_CubeGrid[] Grids; public List<HighlightManager.Box> Boxes; public string Note; public bool BaseFallback; }

        public static void ClearHighlights() { HighlightManager.Clear(); ShipyardRunner.ShowResult("Diff highlights cleared."); }

        // Open the diff-overlay view options (xray + per-category show/hide).
        public static void OpenHighlightOptions() => MyGuiSandbox.AddScreen(new HighlightOptionsScreen());

        public static void VisualDiff(PrEntry pr)
        {
            ShipyardRunner.RunWithBusyThen<DiffPayload>(
                "Building visual diff for PR #" + pr.Number + "...",
                () =>
                {
                    var client = Gh();
                    byte[] prBytes = GetBlueprintBytes(client, pr.HeadBranch, pr.CategoryShip);
                    if (prBytes == null) throw new Exception("Couldn't read the PR's bp.sbc.");
                    MyObjectBuilder_Definitions defs; ulong sz;
                    if (!MyObjectBuilderSerializer.DeserializeXML(prBytes, out defs, out sz) ||
                        defs.ShipBlueprints == null || defs.ShipBlueprints.Length == 0)
                        throw new Exception("Couldn't parse the PR blueprint.");
                    var grids = defs.ShipBlueprints[0].CubeGrids;
                    if (grids == null || grids.Length == 0) throw new Exception("Blueprint has no grids.");

                    var prByPos = BlocksByPositionPrimary(prBytes);
                    bool baseFallback;
                    byte[] mainBytes = PrBaselineBytes(client, pr, out baseFallback);
                    var mainByPos = mainBytes != null ? BlocksByPositionPrimary(mainBytes)
                                                      : new Dictionary<string, MyObjectBuilder_CubeBlock>();

                    var primary = grids[0];
                    double cell = primary.GridSizeEnum == MyCubeSize.Large ? 2.5 : 0.5;

                    // Recover a shifted pivot: realign main INTO the PR's frame before classifying.
                    int matches; Vector3I off = RecoverOffset(PosSubs(mainByPos), PosSubs(prByPos), out matches);
                    var mainShifted = ShiftKeys(mainByPos, off);

                    var boxes = new List<HighlightManager.Box>();
                    int added = 0, repaint = 0, removed = 0, replaced = 0, settings = 0;
                    if (primary.CubeBlocks != null)
                        foreach (var b in primary.CubeBlocks)
                        {
                            string key = PosKey(b.Min);
                            Vector3I bmin, bmax; ObExtent(b, out bmin, out bmax);
                            MyObjectBuilder_CubeBlock mb;
                            if (!mainShifted.TryGetValue(key, out mb)) { boxes.Add(BoxFor(bmin, bmax, cell, "added", "+ " + b.SubtypeName)); added++; }
                            else if (!string.Equals(b.SubtypeName, mb.SubtypeName, StringComparison.Ordinal)) { boxes.Add(BoxFor(bmin, bmax, cell, "changed", "~ " + mb.SubtypeName + "  ->  " + b.SubtypeName)); replaced++; }
                            else if (ColorDiffers(b.ColorMaskHSV, mb.ColorMaskHSV)) { boxes.Add(BoxFor(bmin, bmax, cell, "recolored", "recolored")); repaint++; }
                            else if (SettingsDiffer(mb, b))
                            {
                                // same block, different CustomData (sorter/script loadout): the
                                // config case. Payload feeds the Ctrl+Shift+D line diff.
                                var bx = BoxFor(bmin, bmax, cell, "data", "± data: " + b.SubtypeName + "   (Ctrl+Shift+D)");
                                bx.OldData = SettingsDisplay(mb); bx.NewData = SettingsDisplay(b);
                                boxes.Add(bx); settings++;
                            }
                        }
                    // removed = a main block whose SHIFTED cell isn't in the PR; ghost it in the PR frame.
                    foreach (var kv in mainByPos)
                    {
                        Vector3I mm = kv.Value.Min; Vector3I sm = mm + off;
                        if (!prByPos.ContainsKey(sm.X + "," + sm.Y + "," + sm.Z))
                        { Vector3I rmin, rmax; ObExtent(kv.Value, out rmin, out rmax); boxes.Add(BoxFor(rmin + off, rmax + off, cell, "removed", "- " + kv.Value.SubtypeName)); removed++; }
                    }

                    string note = null;
                    if (mainBytes == null)
                    {
                        // No baseline at all (a brand-new ship path, or one deleted before the PR's
                        // base): with nothing to compare against, EVERY block would read as added - a
                        // meaningless wall of green boxes - so say what's going on instead of drawing it.
                        boxes.Clear();
                        note = "'" + pr.CategoryShip + "' isn't on main (new ship, or deleted),\n" +
                               "so there is no version to diff against - every block would just read 'added'.\n" +
                               "Spawned the PR version with its real paint, no highlight boxes.";
                    }
                    else if (LooksLikeDifferentGrid(matches, mainByPos.Count, prByPos.Count))
                    {
                        boxes.Clear();
                        note = "Spawned the PR version, but it looks like a DIFFERENT grid:\nonly " + matches + " of ~" + Math.Min(mainByPos.Count, prByPos.Count) +
                               " blocks line up even after pivot alignment, so a per-block diff would be noise.\nUse 'Clear Highlights' when done.";
                    }
                    else boxes = CollapseReplacements(boxes);

                    Plugin.Log("ModeA diff " + pr.CategoryShip + ": prBlocks=" + prByPos.Count + " mainBlocks=" + mainByPos.Count +
                        " offset=(" + off.X + "," + off.Y + "," + off.Z + ") matches=" + matches +
                        " added=" + added + " removed=" + removed + " replaced=" + replaced + " repaint=" + repaint +
                        " settings=" + settings + " boxes=" + boxes.Count + (baseFallback ? " (baseline=PR base commit)" : ""));
                    return new DiffPayload { Grids = grids, Boxes = boxes, Note = note, BaseFallback = baseFallback };
                },
                payload =>
                {
                    var ent = SpawnDiffGrids(payload.Grids, "DIFF " + pr.CategoryShip + " PR#" + pr.Number);
                    if (ent != null && payload.Boxes.Count > 0) HighlightManager.Set(ent, payload.Boxes);
                    string msg;
                    if (payload.Note != null) msg = payload.Note;
                    else
                    {
                        msg = "Spawned ~" + (int)SpawnAheadMeters + "m ahead with its real paint + highlight boxes:\n" +
                              "GREEN = added,  RED = removed,  ORANGE = replaced,  CYAN = repainted,\n" +
                              "MAGENTA = custom data changed (aim at it + Ctrl+Shift+D for a line diff).\n" +
                              "Look at a box up close to see what changed.\nUse 'Clear Highlights' (in the browser) when done.";
                        if (payload.BaseFallback)
                            msg += "\n\nNOTE: this ship was DELETED from main after the PR was opened -\n" +
                                   "diffing against the version the author checked out instead.";
                        if (payload.Boxes.Count > HighlightManager.MaxDraw)
                            msg += "\n\nLARGE DIFF: showing the nearest " + HighlightManager.MaxDraw + " of " + payload.Boxes.Count +
                                   " changes - fly closer to reveal the rest.";
                    }
                    // The different-grid warning is important; the color legend is a dismissible tip.
                    if (payload.Note != null) ShipyardRunner.ShowMessage(msg);
                    else ShipyardRunner.ShowResult(msg);
                });
        }

        // Mode B: highlight changes vs main directly on a grid already in the world (no spawn).
        public static void HighlightCurrent(ShipEntry ship, IMyCubeGrid grid)
        {
            ShipyardRunner.RunWithBusyThen<Dictionary<string, MyObjectBuilder_CubeBlock>>(
                "Highlighting changes vs main...",
                () =>
                {
                    byte[] mainBytes = RepoOrLocalBytes(ship.CategoryShip);
                    return mainBytes != null ? BlocksByPositionPrimary(mainBytes) : new Dictionary<string, MyObjectBuilder_CubeBlock>();
                },
                mainByPos =>
                {
                    // Capture the live grid's OB ONCE (one serialization, not one per block) so the data
                    // diff reads CustomData from the same OB shape as the PR-spawn path.
                    var cube = grid as Sandbox.Game.Entities.MyCubeGrid;
                    var liveOb = cube != null ? cube.GetObjectBuilder(false) as MyObjectBuilder_CubeGrid : null;
                    if (liveOb == null || liveOb.CubeBlocks == null)
                    { ShipyardRunner.ShowMessage("Couldn't read that grid's blocks."); return; }
                    double cell = grid.GridSize;

                    // Key by Min (the blueprint's stored cell), build the live block map + a HashSet of cells.
                    var liveByPos = new Dictionary<string, MyObjectBuilder_CubeBlock>(liveOb.CubeBlocks.Count);
                    var live = new HashSet<string>();
                    var liveList = new List<PosSub>(liveOb.CubeBlocks.Count);
                    foreach (var b in liveOb.CubeBlocks)
                    {
                        string k = b.Min.X + "," + b.Min.Y + "," + b.Min.Z;
                        liveByPos[k] = b; live.Add(k);
                        liveList.Add(new PosSub { Pos = b.Min, Sub = b.SubtypeName });
                    }

                    // Recover a shifted pivot: realign main INTO the live grid's frame.
                    int matches; Vector3I off = RecoverOffset(PosSubs(mainByPos), liveList, out matches);
                    var mainShifted = ShiftKeys(mainByPos, off);

                    var boxes = new List<HighlightManager.Box>();
                    int added = 0, repaint = 0, removed = 0, replaced = 0, settings = 0;
                    foreach (var b in liveOb.CubeBlocks)
                    {
                        string key = PosKey(b.Min);
                        Vector3I bmin, bmax; ObExtent(b, out bmin, out bmax);
                        MyObjectBuilder_CubeBlock mb;
                        if (!mainShifted.TryGetValue(key, out mb)) { boxes.Add(BoxFor(bmin, bmax, cell, "added", "+ " + b.SubtypeName)); added++; }
                        else if (!string.Equals(b.SubtypeName, mb.SubtypeName, StringComparison.Ordinal)) { boxes.Add(BoxFor(bmin, bmax, cell, "changed", "~ " + mb.SubtypeName + "  ->  " + b.SubtypeName)); replaced++; }
                        else if (ColorDiffers(b.ColorMaskHSV, mb.ColorMaskHSV)) { boxes.Add(BoxFor(bmin, bmax, cell, "recolored", "recolored")); repaint++; }
                        else if (SettingsDiffer(mb, b))
                        {
                            // same block, different CustomData (sorter/script loadout): magenta box +
                            // payload for the Ctrl+Shift+D line diff.
                            var bx = BoxFor(bmin, bmax, cell, "data", "± data: " + b.SubtypeName + "   (Ctrl+Shift+D)");
                            bx.OldData = SettingsDisplay(mb); bx.NewData = SettingsDisplay(b);
                            boxes.Add(bx); settings++;
                        }
                    }
                    // removed = a main block whose SHIFTED cell isn't live; ghost it in the live frame.
                    foreach (var kv in mainByPos)
                    {
                        Vector3I mm = kv.Value.Min; Vector3I sm = mm + off;
                        if (!live.Contains(sm.X + "," + sm.Y + "," + sm.Z))
                        { Vector3I rmin, rmax; ObExtent(kv.Value, out rmin, out rmax); boxes.Add(BoxFor(rmin + off, rmax + off, cell, "removed", "- " + kv.Value.SubtypeName)); removed++; }
                    }

                    Plugin.Log("ModeB diff " + ship.CategoryShip + ": live=" + liveByPos.Count + " mainBlocks=" + mainByPos.Count +
                        " offset=(" + off.X + "," + off.Y + "," + off.Z + ") matches=" + matches +
                        " added=" + added + " removed=" + removed + " replaced=" + replaced + " repaint=" + repaint + " settings=" + settings);

                    if (LooksLikeDifferentGrid(matches, mainByPos.Count, liveByPos.Count))
                    {
                        HighlightManager.Clear();
                        ShipyardRunner.ShowMessage("That grid doesn't match '" + ship.Name + "' on main:\nonly " + matches + " of ~" +
                            Math.Min(mainByPos.Count, liveByPos.Count) + " blocks line up even after pivot alignment.\nAre you looking at the right ship?");
                        return;
                    }
                    boxes = CollapseReplacements(boxes);
                    HighlightManager.Set(grid, boxes);
                    string m2 = "Highlighting changes vs main on your ship:\n" +
                        "GREEN = added,  RED = removed,  ORANGE = replaced,  CYAN = repainted,\n" +
                        "MAGENTA = custom data changed (aim at it + Ctrl+Shift+D for a line diff).\n" +
                        "Look at a box up close to see what changed.\nUse 'Clear Highlights' when done.";
                    if (boxes.Count > HighlightManager.MaxDraw)
                        m2 += "\n\nLARGE DIFF: showing the nearest " + HighlightManager.MaxDraw + " of " + boxes.Count + " - fly closer for the rest.";
                    ShipyardRunner.ShowResult(m2);
                });
        }

        // Raycast from the camera to find the grid the player is looking at.
        public static bool TryGetLookedAtGrid(out IMyCubeGrid grid)
        {
            grid = null;
            try
            {
                var cam = MyAPIGateway.Session.Camera;
                IHitInfo hit;
                if (MyAPIGateway.Physics.CastRay(cam.Position, cam.Position + cam.WorldMatrix.Forward * DiffRayMeters, out hit) && hit != null)
                    grid = hit.HitEntity as IMyCubeGrid;
            }
            catch (Exception ex) { Plugin.Log("raycast failed: " + ex.Message); }
            return grid != null;
        }

        // Main-thread: reposition grids ~120m ahead of the camera (new ids) and spawn; returns primary entity.
        // forceStatic pins them in place (good for a diff); pass false to keep them placeable/movable.
        private static IMyEntity SpawnDiffGrids(MyObjectBuilder_CubeGrid[] grids, string name, bool forceStatic = true)
        {
            try
            {
                var cam = MyAPIGateway.Session.Camera;
                Vector3D target = cam.Position + cam.WorldMatrix.Forward * SpawnAheadMeters;
                var primary = grids[0];
                Vector3D p0 = primary.PositionAndOrientation.HasValue
                    ? (Vector3D)primary.PositionAndOrientation.Value.Position : Vector3D.Zero;
                Vector3D offset = target - p0;
                foreach (var g in grids)
                {
                    g.DisplayName = name;
                    if (forceStatic) g.IsStatic = true;
                    if (g.PositionAndOrientation.HasValue)
                    {
                        var po = g.PositionAndOrientation.Value;
                        g.PositionAndOrientation = new MyPositionAndOrientation(
                            (Vector3D)po.Position + offset, (Vector3)po.Forward, (Vector3)po.Up);
                    }
                    else
                        g.PositionAndOrientation = new MyPositionAndOrientation(target, (Vector3)cam.WorldMatrix.Forward, (Vector3)cam.WorldMatrix.Up);
                }
                MyAPIGateway.Entities.RemapObjectBuilderCollection(grids);
                IMyEntity primaryEnt = null;
                foreach (var g in grids)
                {
                    var e = MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(g);
                    if (primaryEnt == null) primaryEnt = e;
                }
                Plugin.Log("spawned visual diff: " + name);
                return primaryEnt;
            }
            catch (Exception ex) { Plugin.Log("SpawnDiffGrids failed: " + ex); ShipyardRunner.ShowMessage("Visual diff spawn failed: " + ex.Message); return null; }
        }

        // Install a specific PR's version into the library under a distinct, tagged name.
        public static void InstallPrVersion(PrEntry pr)
        {
            ShipyardRunner.RunWithBusy("Installing PR #" + pr.Number + " version...", () =>
            {
                var client = Gh();
                // Resolve the PR branch's tree ONCE; both files come out of it.
                var tree = GetRefTree(client, pr.HeadBranch);
                byte[] prBytes = tree != null ? TreeFileBytes(client, tree, RootSlash + pr.CategoryShip + "/bp.sbc") : null;
                if (prBytes == null) return "Couldn't read the PR's bp.sbc.";
                string shipName = pr.CategoryShip.Substring(pr.CategoryShip.LastIndexOf('/') + 1);
                string destName = shipName + "_PR" + pr.Number;     // tagged so it's distinct from main
                string dest = Path.Combine(BlueprintsLocal(), destName);
                Directory.CreateDirectory(dest);
                File.WriteAllBytes(Path.Combine(dest, "bp.sbc"), PrepareInstalledBp(prBytes));
                byte[] thumb = TreeFileBytes(client, tree, RootSlash + pr.CategoryShip + "/thumb.png");
                if (thumb != null) File.WriteAllBytes(Path.Combine(dest, "thumb.png"), thumb);
                Plugin.Log("installed PR version -> " + dest);
                return "Installed '" + destName + "' (the PR's version).\nIt appears in F10 as a separate, tagged blueprint.";
            });
        }

        private static byte[] GetBlueprintFileBytes(GitHubClient client, string gitRef, string categoryShip, string file)
        {
            var tree = GetRefTree(client, gitRef);
            return tree == null ? null : TreeFileBytes(client, tree, RootSlash + categoryShip + "/" + file);
        }

        public static void Approve(int number)
        {
            RunRepoOp("Approving PR #" + number + "...", () =>
            {
                var client = Gh();
                client.PullRequest.Review.Create(Auth.RepoOwner, Auth.RepoName, number,
                    new PullRequestReviewCreate { Event = PullRequestReviewEvent.Approve }).GetAwaiter().GetResult();
                return "Approved PR #" + number + ".";
            });
        }

        public static void RequestChanges(int number)
        {
            RunRepoOp("Requesting changes on PR #" + number + "...", () =>
            {
                var client = Gh();
                client.PullRequest.Review.Create(Auth.RepoOwner, Auth.RepoName, number,
                    new PullRequestReviewCreate { Event = PullRequestReviewEvent.RequestChanges, Body = "Changes requested via Shipyard." }).GetAwaiter().GetResult();
                return "Requested changes on PR #" + number + ".";
            });
        }

        // Server-side "rebase": re-apply the PR's ship files on top of CURRENT main (reusing the PR's
        // existing blobs) and force-update the PR branch
        public static void RebasePr(PrEntry pr)
        {
            RunRepoOp("Rebasing PR #" + pr.Number + " onto main...", () =>
            {
                var client = Gh();
                string owner = Auth.RepoOwner, repo = Auth.RepoName;
                string branch = pr.HeadBranch;
                string rel = RootSlash + pr.CategoryShip;

                // The PR's current ship files (their blob SHAs — reused as-is, no re-upload).
                var prRef = client.Git.Reference.Get(owner, repo, "heads/" + branch).GetAwaiter().GetResult();
                var prCommit = client.Git.Commit.Get(owner, repo, prRef.Object.Sha).GetAwaiter().GetResult();
                var prTree = client.Git.Tree.GetRecursive(owner, repo, prCommit.Tree.Sha).GetAwaiter().GetResult();
                var shipFiles = prTree.Tree.Where(x => x.Type == TreeType.Blob && x.Path.StartsWith(rel + "/")).ToList();
                if (shipFiles.Count == 0) throw new Exception("Couldn't find PR #" + pr.Number + "'s ship files - rebase aborted.");

                // New tree = current main + the PR's ship files overlaid; commit on top of main; force-move branch.
                var mainRef = client.Git.Reference.Get(owner, repo, "heads/main").GetAwaiter().GetResult();
                var mainCommit = client.Git.Commit.Get(owner, repo, mainRef.Object.Sha).GetAwaiter().GetResult();
                var newTree = new NewTree { BaseTree = mainCommit.Tree.Sha };
                foreach (var f in shipFiles)
                    newTree.Tree.Add(new NewTreeItem { Path = f.Path, Mode = f.Mode, Type = TreeType.Blob, Sha = f.Sha });
                var treeResp = client.Git.Tree.Create(owner, repo, newTree).GetAwaiter().GetResult();
                var commit = client.Git.Commit.Create(owner, repo,
                    new NewCommit("rebase " + pr.CategoryShip + " onto main", treeResp.Sha, mainRef.Object.Sha)).GetAwaiter().GetResult();
                client.Git.Reference.Update(owner, repo, "heads/" + branch, new ReferenceUpdate(commit.Sha, true)).GetAwaiter().GetResult();

                Plugin.Log("rebased PR #" + pr.Number + " (" + pr.CategoryShip + ") onto main");
                return "Rebased PR #" + pr.Number + " onto current main.\nConflict resolved - Merge it now.";
            });
        }

        public static void Merge(PrEntry pr)
        {
            if (pr.Mergeable == false)
            {
                if (string.IsNullOrEmpty(pr.CategoryShip))
                { ShipyardRunner.ShowMessage("PR #" + pr.Number + " has conflicts and isn't from a ship/... branch.\nResolve it on GitHub web."); return; }
                ShipyardRunner.Confirm("PR #" + pr.Number + " HAS CONFLICTS",
                    "It can't be clean-merged (the branch conflicts with main).\n" +
                    "Rebase it onto current main now? This re-applies the PR's version on top of the latest\n" +
                    "main and resolves the conflict, then you can Merge.",
                    ok => { if (ok) RebasePr(pr); });
                return;
            }
            RunRepoOp("Merging PR #" + pr.Number + "...", () =>
            {
                var client = Gh();
                string me = Auth.Login;   // stored at sign-in; fall back to the API only if it's not cached
                if (string.IsNullOrEmpty(me)) me = client.User.Current().GetAwaiter().GetResult().Login;
                bool iAmOwner = pr.Owner != null && string.Equals(me, pr.Owner, StringComparison.OrdinalIgnoreCase);

                if (pr.Owner != null && !iAmOwner && !pr.OwnerApproved)
                    throw new Exception("Locked: '" + pr.CategoryShip + "' is owned by @" + pr.Owner +
                                        ".\nIt needs their approval before it can be merged.");

                client.PullRequest.Merge(Auth.RepoOwner, Auth.RepoName, pr.Number,
                    new MergePullRequest { MergeMethod = PullRequestMergeMethod.Merge }).GetAwaiter().GetResult();
                if (!string.IsNullOrEmpty(pr.HeadBranch))
                    try { client.Git.Reference.Delete(Auth.RepoOwner, Auth.RepoName, "heads/" + pr.HeadBranch).GetAwaiter().GetResult(); } catch (Exception ex) { Plugin.Log("branch delete failed: " + ex.Message); }
                Plugin.Log("merged PR #" + pr.Number + " (" + pr.CategoryShip + ")");
                return "Merged PR #" + pr.Number + " (" + pr.CategoryShip + ") into main.";
            });
        }

        // Reject/close a PR without merging (and delete its branch).
        public static void ClosePr(PrEntry pr)
        {
            RunRepoOp("Closing PR #" + pr.Number + "...", () =>
            {
                var client = Gh();
                client.PullRequest.Update(Auth.RepoOwner, Auth.RepoName, pr.Number,
                    new PullRequestUpdate { State = ItemState.Closed }).GetAwaiter().GetResult();
                if (!string.IsNullOrEmpty(pr.HeadBranch))
                    try { client.Git.Reference.Delete(Auth.RepoOwner, Auth.RepoName, "heads/" + pr.HeadBranch).GetAwaiter().GetResult(); } catch (Exception ex) { Plugin.Log("branch delete failed: " + ex.Message); }
                Plugin.Log("closed PR #" + pr.Number);
                return "Closed PR #" + pr.Number + " (rejected, branch deleted).";
            });
        }

        // Owner-only: delete a hull from main entirely (files + CODEOWNERS line).
        public static void DeleteShip(ShipEntry ship)
        {
            if (Auth.IsOffline) { LocalDeleteShip(ship.CategoryShip); return; }   // offline: delete + commit locally
            RunRepoOp("Deleting '" + ship.CategoryShip + "' from main...", () =>
            {
                var client = Gh();
                string me = Auth.Login;   // stored at sign-in; fall back to the API only if it's not cached
                if (string.IsNullOrEmpty(me)) me = client.User.Current().GetAwaiter().GetResult().Login;
                var owners = ReadCodeowners(client);
                string owner = owners.ContainsKey(RootSlash + ship.CategoryShip) ? owners[RootSlash + ship.CategoryShip] : null;
                if (owner != null && !string.Equals(me, owner, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Only the owner (@" + owner + ") can delete '" + ship.CategoryShip + "'.");

                string prefix = RootSlash + ship.CategoryShip + "/";
                var tree = client.Git.Tree.GetRecursive(Auth.RepoOwner, Auth.RepoName, "main").GetAwaiter().GetResult();
                var files = tree.Tree.Where(x => x.Type == TreeType.Blob && x.Path.StartsWith(prefix)).ToList();
                if (files.Count == 0) throw new Exception("Ship not found on main: " + ship.CategoryShip);

                foreach (var f in files)
                    client.Repository.Content.DeleteFile(Auth.RepoOwner, Auth.RepoName, f.Path,
                        new DeleteFileRequest("Delete " + ship.CategoryShip, f.Sha, "main")).GetAwaiter().GetResult();

                // remove the CODEOWNERS line
                try
                {
                    var co = client.Repository.Content.GetAllContents(Auth.RepoOwner, Auth.RepoName, ".github/CODEOWNERS").GetAwaiter().GetResult();
                    if (co.Count > 0)
                    {
                        string body = co[0].Content ?? "";
                        // Remove the EXACT owner line for this ship (tolerant of tab/space), not a path
                        // prefix - so deleting a ship can't strip a nested ship's owner line.
                        var kept = CodeownersWithout(body, RootSlash + ship.CategoryShip);
                        string newCo = string.Join("\n", kept).TrimEnd('\n') + "\n";
                        client.Repository.Content.UpdateFile(Auth.RepoOwner, Auth.RepoName, ".github/CODEOWNERS",
                            new UpdateFileRequest("Remove " + ship.CategoryShip + " owner", newCo, co[0].Sha, "main")).GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex) { Plugin.Log("CODEOWNERS cleanup failed: " + ex.Message); }

                Plugin.Log("deleted ship " + ship.CategoryShip + " from main");
                return "Deleted '" + ship.CategoryShip + "' from main (" + files.Count + " files).";
            });
        }
    }

    // One open pull request as shown in the Pending Changes screen.
    public class PrEntry
    {
        public int Number;
        public string Title;
        public string CategoryShip;   // "PvP/Red-Ship_1" (from the ship/ or checkout/ branch)
        public string HeadBranch;     // the PR's actual head branch (delete THIS on merge/close)
        public string BaseSha;        // main's commit when the PR was opened (diff fallback if the ship left main)
        public string Author;
        public string Owner;          // ship owner from CODEOWNERS (or null)
        public bool OwnerApproved;
        public bool IsMine;
        public bool NeedsMyReview;    // I own this ship and someone else submitted it
        public bool? Mergeable;
        public string ThumbPath;
        public string HtmlUrl;

        public string Group()
        {
            if (NeedsMyReview) return "Needs your review";
            if (IsMine) return "Your submissions";
            return "Others";
        }

        public string Status()
        {
            if (Mergeable == false) return "conflicts";
            if (Owner == null) return "no owner";
            if (OwnerApproved) return "approved";
            if (IsMine) return "yours";
            return "needs @" + Owner;
        }
    }
}
