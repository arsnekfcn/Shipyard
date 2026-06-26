using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Sandbox.ModAPI;
using VRage.Input;
using VRage.Plugins;
using VRage.Utils;

namespace ShipyardPlugin
{
    // Shipyard plugin entry point. Injects the F10 "Shipyard" button (Harmony), drives the
    // per-frame hooks (chat commands, open hotkey, diff highlights), and loads the embedded
    // dependencies. All GitHub work lives in ShipyardApi.
    public class Plugin : IPlugin
    {
        public const string Id = "shipyard";

        private Harmony _harmony;

        public static string PluginDir =>
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        public void Init(object gameInstance)
        {
            Log("Init: loading");
            ExtractLogo();        // write the embedded logo crest to a file the GUI can load by path
#if LOCAL_BUILD
            // Single-file local build only: the embedded Octokit/LibGit2Sharp managed DLLs need a resolver.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveSibling;
#endif
            // Point LibGit2Sharp at the native git2 (offline-mode engine). Both build models need this:
            // LOCAL extracts the embedded native; Pulsar (from-source) locates it in Pulsar's NuGet cache.
            InitLocalGit();
            try
            {
                _harmony = new Harmony(Id);
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log("Init: Harmony patches applied (pluginDir=" + PluginDir + ")");
            }
            catch (Exception ex) { Log("Init FAILED: " + ex); }
        }

        // Read a stream to the end (Stream.Read may return fewer bytes than asked, even mid-stream).
        // Assumes a SEEKABLE stream (uses s.Length); all callers pass GetManifestResourceStream
        // results. Do not reuse on a non-seekable (network/compressed) stream - .Length would throw.
        private static byte[] ReadAll(Stream s)
        {
            var buf = new byte[s.Length];
            int off = 0, n;
            while (off < buf.Length && (n = s.Read(buf, off, buf.Length - off)) > 0) off += n;
            return buf;
        }

        // The logo is embedded in the DLL (single-file distribution) but the GUI loads textures by PATH,
        // so write it out next to the plugin (fallback: %APPDATA%\Shipyard) where Brand.LogoPath looks.
        private static void ExtractLogo()
        {
            try
            {
                byte[] buf;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png"))
                {
                    if (s == null) return;
                    buf = ReadAll(s);
                }
                string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shipyard");
                foreach (var dest in new[] { Path.Combine(PluginDir ?? "", "logo.png"), Path.Combine(appDir, "logo.png") })
                {
                    string parent = Path.GetDirectoryName(dest);
                    if (string.IsNullOrEmpty(parent)) continue;
                    try
                    {
                        // Both destinations are independent fallbacks the GUI may look in, so refresh
                        // each one. Same size = up to date; otherwise (first run, or the crest changed
                        // in an update) write it out fresh. 'continue' (not 'return') so the next
                        // destination is still visited.
                        if (File.Exists(dest) && new FileInfo(dest).Length == buf.Length) continue;
                        Directory.CreateDirectory(parent); File.WriteAllBytes(dest, buf); Log("logo extracted -> " + dest); continue;
                    }
                    catch (Exception ex) { Log("logo write failed (" + dest + "): " + ex.Message); }
                }
            }
            catch (Exception ex) { Log("ExtractLogo failed: " + ex.Message); }
        }

        // LibGit2Sharp's NATIVE git2-*.dll must be a real file on disk with GlobalSettings.NativeLibraryPath
        // pointed at its folder. The two build models supply it differently (see the branches below). If it
        // can't be located, only OFFLINE (local-git) mode is affected; the online GitHub path (Octokit) is fine.
        private const string NativeGitDll = "git2-a418d9d.dll";   // tied to LibGit2Sharp 0.30.0 / NativeBinaries 2.0.322
        private static void InitLocalGit()
        {
            try
            {
#if LOCAL_BUILD
                // LOCAL single-file build: the native git2 is EMBEDDED -> extract to %APPDATA%\Shipyard\native.
                string nativeDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shipyard", "native");
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(NativeGitDll))
                {
                    if (s == null) { Log("InitLocalGit: embedded native git2 missing - offline/local-git mode unavailable (online GitHub mode is unaffected)"); return; }
                    Directory.CreateDirectory(nativeDir);
                    string dest = Path.Combine(nativeDir, NativeGitDll);
                    byte[] buf = ReadAll(s);
                    // size-compare so we only rewrite on first run / version bump (the dll may be loaded/locked)
                    if (!File.Exists(dest) || new FileInfo(dest).Length != buf.Length)
                        try { File.WriteAllBytes(dest, buf); } catch (Exception ex) { Log("native write skipped: " + ex.Message); }
                    LibGit2Sharp.GlobalSettings.NativeLibraryPath = nativeDir;
                    Log("LibGit2Sharp ready (embedded): libgit2 " + LibGit2Sharp.GlobalSettings.Version);
                }
#else
                // PULSAR from-source build: LibGit2Sharp.NativeBinaries is restored via NuGet, but Pulsar does
                // NOT copy the native git2 next to the plugin nor set NativeLibraryPath (its build props don't
                // run for the Roslyn-from-source compile) -> locate it in Pulsar's NuGet cache and point there.
                string found = FindNativeGitDir();
                if (found != null)
                {
                    LibGit2Sharp.GlobalSettings.NativeLibraryPath = found;
                    Log("LibGit2Sharp ready (resolved native): " + found + " -> libgit2 " + LibGit2Sharp.GlobalSettings.Version);
                }
                else
                    Log("InitLocalGit: native git2 not found - offline/local-git mode unavailable (online GitHub mode is unaffected)");
#endif
            }
            catch (Exception ex) { Log("InitLocalGit failed: " + ex.Message); }
        }

#if !LOCAL_BUILD
        private const string NativeBinariesPkg = "LibGit2Sharp.NativeBinaries";
        private const string NativeBinariesVer = "2.0.322";   // transitive of LibGit2Sharp 0.30.0
        // Locate the folder with the native git2-*.dll for the Pulsar build. Pulsar restores it into its OWN
        // NuGet cache (portable install: <PulsarRoot>\Legacy\NuGet\packages\<Pkg>.<Ver>\runtimes\<rid>\native),
        // not next to the plugin and not via GlobalSettings auto-resolution. Probe the known package layouts
        // first (fast, exact), then a bounded recursive sweep of small roots; always prefer the process RID.
        private static string FindNativeGitDir()
        {
            string rid = Environment.Is64BitProcess ? "win-x64" : "win-x86";
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string entryDir = null;
            try { entryDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? ""); } catch { }

            var pkgRoots = new[]
            {
                entryDir != null ? Path.Combine(entryDir, "Legacy", "NuGet", "packages") : null,  // portable Pulsar (SE1/Legacy)
                entryDir != null ? Path.Combine(entryDir, "NuGet", "packages") : null,
                Path.Combine(appData, "Pulsar", "NuGet", "packages"),                              // non-portable Pulsar
                Path.Combine(userProfile, ".nuget", "packages"),                                   // global NuGet cache
            };
            foreach (var root in pkgRoots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var pkgDir in new[]
                {
                    Path.Combine(root, NativeBinariesPkg + "." + NativeBinariesVer),               // Pulsar layout: Pkg.Ver
                    Path.Combine(root, NativeBinariesPkg.ToLowerInvariant(), NativeBinariesVer),   // global NuGet: pkg/ver
                })
                {
                    string dir = Path.Combine(pkgDir, "runtimes", rid, "native");
                    try { if (File.Exists(Path.Combine(dir, NativeGitDll))) return dir; } catch { }
                }
            }
            // Bounded fallback: recurse the small, likely roots only (never the huge global cache).
            foreach (var root in new[] { entryDir, PluginDir })
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, NativeGitDll, SearchOption.AllDirectories))
                        if (f.IndexOf(rid, StringComparison.OrdinalIgnoreCase) >= 0) return Path.GetDirectoryName(f);
                }
                catch (Exception ex) { Log("native sweep in " + root + " skipped: " + ex.Message); }
            }
            return null;
        }
#endif

#if LOCAL_BUILD
        private static Assembly ResolveSibling(object sender, ResolveEventArgs e)
        {
            try
            {
                string name = new AssemblyName(e.Name).Name;
                var self = Assembly.GetExecutingAssembly();
                using (var s = self.GetManifestResourceStream(name + ".dll"))
                {
                    if (s != null)
                    {
                        Log("resolving " + name + " from embedded resource");
                        return Assembly.Load(ReadAll(s));
                    }
                }
            }
            catch (Exception ex) { Log("ResolveSibling failed: " + ex.Message); }
            return null;
        }
#endif

        public void Update()
        {
            try { ChatCommands.Tick(); } catch { /* never spam the sim loop */ }
            try { TryHotkey(); } catch { /* never spam the sim loop */ }
            try { TryDataDiffKey(); } catch { /* never spam the sim loop */ }
            try { HighlightManager.Draw(); } catch { /* never spam the sim loop */ }
        }

        // Ctrl+Shift+D while diff highlights are up: open the line-by-line settings/custom-data
        // diff for the magenta box under the crosshair (the "what changed in this cargo?" window).
        private static void TryDataDiffKey()
        {
            if (!HighlightManager.Active) return;
            var inp = MyInput.Static;
            if (inp == null || !inp.IsAnyCtrlKeyPressed() || !inp.IsAnyShiftKeyPressed() || !inp.IsNewKeyPressed(MyKeys.D)) return;
            string label, oldD, newD;
            if (!HighlightManager.TryGetLookedAtData(out label, out oldD, out newD))
            { try { MyAPIGateway.Utilities?.ShowNotification("Aim at a MAGENTA '± data' box, then press Ctrl+Shift+D.", 4000); } catch { } return; }
            Sandbox.Graphics.GUI.MyGuiSandbox.AddScreen(new TextDiffScreen(label, oldD, newD));
        }

        // ---- configurable hotkey to open the Shipyard directly (in addition to the F10 button) ----
        private static bool _hkLoaded;
        private static MyKeys _hkKey = MyKeys.None;
        private static bool _hkCtrl, _hkShift;
        private static int _hkCooldown;

        // Default open hotkey = Ctrl+Shift+S
        private const string DefaultHotkeyLine = "Ctrl+Shift+S";
        private static void LoadHotkey()
        {
            _hkLoaded = true;
            _hkKey = MyKeys.S; _hkCtrl = true; _hkShift = true;   // default: Ctrl+Shift+S
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shipyard", "hotkey.txt");
                if (File.Exists(path))
                {
                    // Parse the FIRST real line only (a # comment line's example keys must NOT leak in).
                    string spec = null;
                    foreach (var ln in File.ReadLines(path))
                    {
                        string t = ln.Trim();
                        if (t.Length == 0 || t.StartsWith("#")) continue;
                        spec = t; break;
                    }
                    if (spec != null)
                    {
                        bool ctrl = false, shift = false, alt = false; MyKeys key = MyKeys.None;
                        foreach (var raw in spec.Split('+', ' ', ','))
                        {
                            string tok = raw.Trim();
                            if (tok.Length == 0) continue;
                            if (tok.Equals("ctrl", StringComparison.OrdinalIgnoreCase) || tok.Equals("control", StringComparison.OrdinalIgnoreCase)) ctrl = true;
                            else if (tok.Equals("shift", StringComparison.OrdinalIgnoreCase)) shift = true;
                            else if (tok.Equals("alt", StringComparison.OrdinalIgnoreCase)) alt = true;
                            else { MyKeys k; if (Enum.TryParse(tok, true, out k)) key = k; }
                        }
                        // 'B' is reserved by SE (free-rotation + blueprint commands). Don't use.
                        if (key == MyKeys.B)
                        {
                            Log("hotkey: '" + spec + "' uses B, which SE reserves for blueprints - migrating to " + DefaultHotkeyLine);
                            try { File.WriteAllText(path, DefaultHotkeyLine + HotkeyComment()); } catch { }
                        }
                        // Alt can't be gated with the input API we use. Honoring "Alt+X" as bare "X"
                        // would surprise the user, so reject the spec and keep the default instead.
                        else if (alt) Log("hotkey: 'Alt' is not supported (" + spec + ") - using default " + DefaultHotkeyLine);
                        else if (key != MyKeys.None) { _hkKey = key; _hkCtrl = ctrl; _hkShift = shift; }
                    }
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, DefaultHotkeyLine + HotkeyComment());
                }
            }
            catch (Exception ex) { Log("hotkey load failed: " + ex.Message); }
            Log("hotkey: " + (_hkCtrl ? "Ctrl+" : "") + (_hkShift ? "Shift+" : "") + _hkKey);
        }

        // Avoid B (SE blueprint/rotation), C/X/Delete (SE Ctrl+Shift binds) in the suggested examples.
        private static string HotkeyComment() =>
            "\r\n# Shipyard open hotkey. Avoid B (SE uses it for blueprints).\r\n" +
            "# Examples:  Ctrl+Shift+S  |  Home  |  OemBackslash  |  Ctrl+J  |  Shift+F6\r\n";

        private static void TryHotkey()
        {
            if (!_hkLoaded) LoadHotkey();
            if (_hkCooldown > 0) { _hkCooldown--; return; }
            if (_hkKey == MyKeys.None || MyAPIGateway.Session == null) return;
            // Don't fire while the player is typing in chat (a modifier-less hotkey would trigger mid-sentence).
            try { if (MyAPIGateway.Gui != null && MyAPIGateway.Gui.ChatEntryVisible) return; } catch { }
            var input = MyAPIGateway.Input;
            if (input == null) return;
            // Required modifiers must be held AND non-required ones must be absent, so an EXACT match.
            // (Without the negative checks a modifier-less hotkey like 'S' would also fire during Ctrl+S.)
            if (_hkCtrl != input.IsAnyCtrlKeyPressed()) return;
            if (_hkShift != input.IsAnyShiftKeyPressed()) return;
            if (input.IsNewKeyPressed(_hkKey))
            {
                _hkCooldown = 60;   // ~1s guard so it doesn't re-open repeatedly
                try { ShipyardApi.OpenShipyard(null); } catch (Exception ex) { Log("hotkey open failed: " + ex.Message); }
            }
        }

        public void Dispose()
        {
            try { ChatCommands.Unhook(); } catch { }
            try { _harmony?.UnpatchAll(Id); } catch { }
            Log("Dispose");
        }

        public static void Log(string msg)
        {
            MyLog.Default?.WriteLineAndConsole("[" + Id + "] " + msg);
        }
    }
}
