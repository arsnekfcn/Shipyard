using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ShipyardPlugin
{
    // Per-user auth + repo config. The access token is DPAPI-encrypted (current Windows user); the
    // target repo, login, and optional custom OAuth client id live in config.json. Multi-user ready:
    // every user signs in (device flow) to their OWN GitHub account and points at THEIR OWN repo.
    internal static class Auth
    {
        // THREADING: config getters lazily call Load() on first touch and SetXxx()/Save() mutate shared
        // state. GUI handlers run on the main thread while background GitHub/git work (ShipyardRunner)
        // reads Token()/RepoOwner/Mode. _gate serializes the lazy Load() and the token cache so two
        // threads can't both run Load() / reassign collections or observe a half-filled token cache.
        // Field reads after _loaded is set are treated as effectively read-only for the session.
        private static readonly object _gate = new object();

        // Public client id of the shared "Shipyard" GitHub OAuth App (device flow; not a secret).
        // Users can override this with their own app's client id in Settings.
        public const string DefaultClientId = "Ov23lizbnLPkuoTXVlca";

        private static string ConfigDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shipyard");
        private static string TokenPath => Path.Combine(ConfigDir, "token.dat");
        private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

        // A shipyard the user has signed into before (for quick switching on the auth screen).
        public class SavedRepo
        {
            public string Owner, Name, Root;
            public string Key => Owner + "/" + Name;
        }

        private static bool _loaded;
        private static string _clientId, _owner, _repo, _login, _root;
        private static bool _hidePopups, _xray;
        private static List<SavedRepo> _repos = new List<SavedRepo>();
        // Offline mode: "" = not chosen yet (first boot), "online" = GitHub, "offline" = local git only.
        private static string _mode, _localPath, _localAuthor;
        // Diff highlight view prefs: which change categories are HIDDEN (default none). See HighlightManager.
        private static HashSet<string> _diffHidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int _highlightCount = 400;   // max boxes drawn at once
        private static int _highlightDist = 0;      // metres: only highlight blocks within this range (0 = unlimited)
        private static Dictionary<string, string> _diffColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string _bgColor;          // panel background RRGGBB (null = default)
        private static float _bgAlpha = -1f;     // panel background opacity 0..1 (-1 = unset -> default)
        private const string DefaultBgColor = "090A0D";
        private const float DefaultBgAlpha = 0.98f;

        // Default category colors (RRGGBB). Configurable per-user (e.g. for color-blindness).
        // NOTE: these category keys MUST stay in lockstep with HighlightManager.Categories
        // ("added"/"removed"/"changed"/"recolored"/"data"); drift silently mis-maps colors/visibility.
        private static string DefaultColor(string cat)
        {
            switch (cat)
            {
                case "added": return "00FF00";
                case "removed": return "FF0000";
                case "changed": return "FFA500";
                case "recolored": return "00FFFF";
                case "data": return "FF00FF";
                default: return "FFFFFF";
            }
        }
        private static string NormalizeHex(string hex)
        {
            string h = (hex ?? "").Trim().TrimStart('#').ToUpperInvariant();
            return Regex.IsMatch(h, "^[0-9A-F]{6}$") ? h : null;
        }

        private static void Load()
        {
            lock (_gate)
            {
                if (_loaded) return;   // another thread won the race to Load()
                LoadLocked();
            }
        }

        private static void LoadLocked()
        {
            _clientId = _owner = _repo = _login = _root = null;
            _mode = _localPath = _localAuthor = null;
            _hidePopups = _xray = false;
            _repos = new List<SavedRepo>();
            _diffHidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _highlightCount = 400; _highlightDist = 0;
            _diffColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _bgColor = null; _bgAlpha = -1f;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string j = File.ReadAllText(ConfigPath);
                    _clientId = Get(j, "clientId");
                    _owner = Get(j, "repoOwner");
                    _repo = Get(j, "repoName");
                    _login = Get(j, "login");
                    _root = Get(j, "rootFolder");
                    _hidePopups = Get(j, "hidePopups") == "true";
                    _mode = Get(j, "mode");
                    _localPath = Get(j, "localPath");
                    _localAuthor = Get(j, "localAuthor");
                    _xray = Get(j, "xray") == "true";
                    foreach (var c in (Get(j, "diffHidden") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        _diffHidden.Add(c.Trim());
                    // Only assign on a successful parse so the 400 default survives a missing/empty key
                    // (TryParse writes 0 to the out-param on failure, which would silently lose the default).
                    if (int.TryParse(Get(j, "highlightCount"), out var hc)) _highlightCount = hc;
                    if (int.TryParse(Get(j, "highlightDist"), out var hd)) _highlightDist = hd;
                    foreach (var kv in (Get(j, "diffColors") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = kv.IndexOf('=');
                        if (eq > 0) { var h = NormalizeHex(kv.Substring(eq + 1)); if (h != null) _diffColors[kv.Substring(0, eq).Trim()] = h; }
                    }
                    _bgColor = NormalizeHex(Get(j, "bgColor"));
                    float ba;
                    if (float.TryParse(Get(j, "bgAlpha"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out ba)) _bgAlpha = ba;
                    // "repos": [{"owner":"a","name":"b","root":"Fleet"}, ...]
                    var arr = Regex.Match(j, "\"repos\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
                    if (arr.Success)
                        foreach (Match o in Regex.Matches(arr.Groups[1].Value, "\\{[^}]*\\}"))
                        {
                            var r = new SavedRepo { Owner = Get(o.Value, "owner"), Name = Get(o.Value, "name"), Root = Get(o.Value, "root") };
                            if (!string.IsNullOrEmpty(r.Owner) && !string.IsNullOrEmpty(r.Name)) _repos.Add(r);
                        }
                }
            }
            catch (Exception ex) { Plugin.Log("Auth.Load failed: " + ex.Message); }
            _loaded = true;
        }

        // Per-user UI pref: show operation results as white HUD notifications instead of modal pop-ups
        // (set by ticking "don't show this again" on any result box).
        public static bool HidePopups { get { if (!_loaded) Load(); return _hidePopups; } }
        public static void SetHidePopups(bool v) { if (!_loaded) Load(); _hidePopups = v; Save(); }

        // Diff-highlight view prefs (per-user). Xray = draw boxes through blocks. Per-category show/hide
        // by canonical key: added / removed / changed / recolored / data.
        public static bool XrayHighlights { get { if (!_loaded) Load(); return _xray; } }
        public static void SetXray(bool v) { if (!_loaded) Load(); _xray = v; Save(); }
        public static bool IsDiffShown(string category) { if (!_loaded) Load(); return string.IsNullOrEmpty(category) || !_diffHidden.Contains(category); }
        public static void SetDiffShown(string category, bool shown)
        {
            if (!_loaded) Load();
            if (string.IsNullOrEmpty(category)) return;
            if (shown) _diffHidden.Remove(category); else _diffHidden.Add(category);
            Save();
        }

        public static int HighlightCount { get { if (!_loaded) Load(); return Math.Min(2000, Math.Max(10, _highlightCount <= 0 ? 400 : _highlightCount)); } }
        public static void SetHighlightCount(int v) { if (!_loaded) Load(); _highlightCount = Math.Min(2000, Math.Max(10, v)); Save(); }
        public static int HighlightDistance { get { if (!_loaded) Load(); return _highlightDist < 0 ? 0 : _highlightDist; } }   // 0 = unlimited
        public static void SetHighlightDistance(int v) { if (!_loaded) Load(); _highlightDist = v < 0 ? 0 : v; Save(); }
        public static string DiffColorHex(string cat)
        {
            if (!_loaded) Load();
            return cat != null && _diffColors.TryGetValue(cat, out var h) && !string.IsNullOrEmpty(h) ? h : DefaultColor(cat);
        }
        // Panel background, applied to ALL Shipyard screens (read live by Brand.Bg). Color is RRGGBB;
        // opacity is 0..1. Configurable in Settings.
        public static string BgColorHex { get { if (!_loaded) Load(); return string.IsNullOrEmpty(_bgColor) ? DefaultBgColor : _bgColor; } }
        public static void SetBgColorHex(string hex) { if (!_loaded) Load(); var h = NormalizeHex(hex); if (h == null) return; _bgColor = h; Save(); }
        public static float BgAlpha { get { if (!_loaded) Load(); return _bgAlpha < 0f ? DefaultBgAlpha : Math.Min(1f, Math.Max(0f, _bgAlpha)); } }
        public static void SetBgAlpha(float v) { if (!_loaded) Load(); _bgAlpha = Math.Min(1f, Math.Max(0f, v)); Save(); }

        public static void SetDiffColorHex(string cat, string hex)
        {
            if (!_loaded) Load();
            if (string.IsNullOrEmpty(cat)) return;
            var h = NormalizeHex(hex);
            if (h == null) return;          // ignore malformed input (keep current)
            _diffColors[cat] = h; Save();
        }

        private static string Get(string json, string key)
        {
            // Match a JSON string value, honoring backslash escapes (so it doesn't stop at an escaped
            // quote), then UN-escape it (inverse of J): J writes the escapes, Unescape reverses them, and
            // Get must un-escape captured values or a value containing backslashes/quotes would round-trip
            // corrupted. NOTE: this is an unanchored match over the whole document, so Get must only be
            // used for UNIQUE top-level keys (see the nested "repos" handling, which parses per-object).
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Unescape(m.Groups[1].Value) : null;
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i++; }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // JSON string escape for the hand-rolled writer: escapes backslashes and quotes so arbitrary
        // textbox input (paths, names) can't corrupt config.json. Inverse of Unescape (read back by Get).
        private static string J(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        // Batch many SetXxx calls into ONE disk write: the GUI Settings screen applies ~9 settings at once,
        // and each setter calls Save(). BeginBatch suppresses the per-setter writes; EndBatch writes once.
        private static bool _suppressSave;
        public static void BeginBatch() { _suppressSave = true; }
        public static void EndBatch() { _suppressSave = false; Save(); }

        private static void Save()
        {
            if (_suppressSave) return;   // inside a BeginBatch/EndBatch block - defer to the single EndBatch write
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var sb = new StringBuilder("{\n");
                sb.Append("  \"clientId\": \"").Append(J(_clientId)).Append("\",\n");
                sb.Append("  \"repoOwner\": \"").Append(J(_owner)).Append("\",\n");
                sb.Append("  \"repoName\": \"").Append(J(_repo)).Append("\",\n");
                sb.Append("  \"rootFolder\": \"").Append(J(_root)).Append("\",\n");
                sb.Append("  \"hidePopups\": \"").Append(_hidePopups ? "true" : "false").Append("\",\n");
                sb.Append("  \"mode\": \"").Append(J(_mode)).Append("\",\n");
                sb.Append("  \"localPath\": \"").Append(J(_localPath)).Append("\",\n");
                sb.Append("  \"localAuthor\": \"").Append(J(_localAuthor)).Append("\",\n");
                sb.Append("  \"xray\": \"").Append(_xray ? "true" : "false").Append("\",\n");
                sb.Append("  \"diffHidden\": \"").Append(J(string.Join(",", _diffHidden))).Append("\",\n");
                sb.Append("  \"highlightCount\": \"").Append(_highlightCount).Append("\",\n");
                sb.Append("  \"highlightDist\": \"").Append(_highlightDist).Append("\",\n");
                sb.Append("  \"diffColors\": \"").Append(J(string.Join(";", _diffColors.Select(kv => kv.Key + "=" + kv.Value)))).Append("\",\n");
                sb.Append("  \"bgColor\": \"").Append(string.IsNullOrEmpty(_bgColor) ? DefaultBgColor : _bgColor).Append("\",\n");
                sb.Append("  \"bgAlpha\": \"").Append((_bgAlpha < 0f ? DefaultBgAlpha : _bgAlpha).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\",\n");
                sb.Append("  \"repos\": [");
                for (int i = 0; i < _repos.Count; i++)
                {
                    var r = _repos[i];
                    sb.Append(i > 0 ? ",\n    " : "")
                      .Append("{\"owner\": \"").Append(J(r.Owner)).Append("\", \"name\": \"").Append(J(r.Name))
                      .Append("\", \"root\": \"").Append(J(r.Root)).Append("\"}");
                }
                sb.Append("],\n");
                sb.Append("  \"login\": \"").Append(J(_login)).Append("\"\n}\n");
                File.WriteAllText(ConfigPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Plugin.Log("Auth.Save failed: " + ex.Message); }
        }

        public static string ClientId { get { if (!_loaded) Load(); return string.IsNullOrEmpty(_clientId) ? DefaultClientId : _clientId; } }
        public static string CustomClientId { get { if (!_loaded) Load(); return _clientId ?? ""; } }
        public static string RepoOwner { get { if (!_loaded) Load(); return _owner ?? ""; } }
        public static string RepoName { get { if (!_loaded) Load(); return _repo ?? ""; } }
        public static string Login { get { if (!_loaded) Load(); return _login ?? ""; } }
        // Top-level folder under which all ships live (per-repo). User-set; defaults to a neutral "Fleet".
        public static string RootFolder { get { if (!_loaded) Load(); return string.IsNullOrWhiteSpace(_root) ? "Fleet" : _root.Trim('/', ' '); } }

        // ---- mode (online GitHub vs offline local git) ----
        // "online" by default.
        public static bool IsOffline { get { if (!_loaded) Load(); return _mode == "offline"; } }
        public static string Mode => IsOffline ? "offline" : "online";
        // Has the user made a first-boot Online/Offline choice yet? (false => show the chooser)
        public static bool ModeChosen { get { if (!_loaded) Load(); return !string.IsNullOrEmpty(_mode) || HasToken; } }
        // Local repo dir for offline mode (the local shipyard's working tree + .git).
        public static string LocalRepoPath { get { if (!_loaded) Load(); return _localPath ?? ""; } }
        // Author handle stamped on local commits (no GitHub login offline). Defaults to the Windows user.
        public static string LocalAuthor { get { if (!_loaded) Load(); return string.IsNullOrWhiteSpace(_localAuthor) ? Environment.UserName : _localAuthor; } }

        public static void SetMode(string mode) { if (!_loaded) Load(); _mode = (mode == "offline" ? "offline" : "online"); Save(); }
        public static void SetLocalRepo(string path, string author)
        {
            if (!_loaded) Load();
            _localPath = (path ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(author)) _localAuthor = author.Trim();
            Save();
        }

        // Signed in if the encrypted token file exists OR we hold a token in memory this session (the
        // latter covers the rare case where DPAPI persistence failed but sign-in otherwise succeeded).
        public static bool HasToken => File.Exists(TokenPath) || (_tokLoaded && !string.IsNullOrEmpty(_tok));
        // Offline: configured once a local repo path is set (no token/owner needed). Online: as before.
        public static bool IsConfigured => IsOffline
            ? !string.IsNullOrEmpty(LocalRepoPath)
            : (HasToken && !string.IsNullOrEmpty(RepoOwner) && !string.IsNullOrEmpty(RepoName));

        private static string ReadToken()
        {
            try
            {
                byte[] enc = File.ReadAllBytes(TokenPath);
                byte[] dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch (Exception ex) { Plugin.Log("ReadToken failed: " + ex.Message); return null; }
        }

        // Cached token: DPAPI-decrypt once per session instead of on every GitHub call. 'Rev' bumps on
        // any token change so a cached GitHubClient can invalidate.
        private static string _tok; private static bool _tokLoaded;
        public static int Rev { get; private set; }
        public static string Token()
        {
            if (!_tokLoaded)
                lock (_gate)
                    if (!_tokLoaded) { _tok = ReadToken(); _tokLoaded = true; }
            return _tok;
        }

        public static void WriteToken(string token)
        {
            // In-memory first, so sign-in succeeds and the session works even if encrypted persistence
            // fails. DPAPI is Windows-only; under Proton/Wine CryptProtectData
            // usually works but isn't guaranteed. Never let a Protect() failure crash sign-in. Worst case:
            // the token lives for this session and the user re-auths next launch (logged, not fatal).
            lock (_gate) { _tok = token; _tokLoaded = true; Rev++; }   // serialize with Token()'s cache read
            try
            {
                Directory.CreateDirectory(ConfigDir);
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(TokenPath, enc);
            }
            catch (Exception ex)
            {
                Plugin.Log("WriteToken: encrypted persist failed (" + ex.Message + "); token kept for this session only.");
                try { if (File.Exists(TokenPath)) File.Delete(TokenPath); }   // best-effort: leave no stale/garbage token file
                catch (Exception ex2) { Plugin.Log("WriteToken: stale token cleanup failed: " + ex2.Message); }
            }
        }

        public static void SetLogin(string login) { if (!_loaded) Load(); _login = login; Save(); }
        public static void SetRepo(string owner, string repo) { if (!_loaded) Load(); _owner = owner; _repo = repo; RememberCurrent(); Save(); }

        // ---- saved shipyards (quick switching on the auth screen) ----
        public static List<SavedRepo> SavedRepos { get { if (!_loaded) Load(); return _repos; } }

        // Record the active owner/repo/root in the saved list (called whenever the repo or root is
        // set, so any shipyard the user signs into is offered for quick switching later).
        private static void RememberCurrent()
        {
            if (string.IsNullOrEmpty(_owner) || string.IsNullOrEmpty(_repo)) return;
            var hit = _repos.FirstOrDefault(r => string.Equals(r.Owner, _owner, StringComparison.OrdinalIgnoreCase)
                                              && string.Equals(r.Name, _repo, StringComparison.OrdinalIgnoreCase));
            if (hit == null) _repos.Add(new SavedRepo { Owner = _owner, Name = _repo, Root = _root ?? "" });
            else hit.Root = _root ?? hit.Root;
        }

        public static void ForgetRepo(string owner, string name)
        {
            if (!_loaded) Load();
            _repos.RemoveAll(r => string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            Save();
        }
        public static void SetClientId(string clientId) { if (!_loaded) Load(); _clientId = clientId; Save(); }
        public static void SetRootFolder(string root) { if (!_loaded) Load(); _root = (root ?? "").Trim('/', ' '); RememberCurrent(); Save(); }

        public static void SignOut()
        {
            try { if (File.Exists(TokenPath)) File.Delete(TokenPath); } catch (Exception ex) { Plugin.Log("SignOut failed: " + ex.Message); }
            lock (_gate) { _tok = null; _tokLoaded = false; Rev++; }   // serialize with Token()'s cache read
            if (!_loaded) Load();
            // Clear only the login. Owner/repo/root are KEPT: SignOut also runs on token expiry
            // (WipeIfExpired), and wiping the configured repo every ~8h would be a worse regression than
            // the low-severity account-switch footgun (the saved-repos chooser + access check cover that).
            _login = "";
            Save();
        }
    }
}
