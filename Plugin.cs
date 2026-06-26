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
    // per-frame hooks (chat commands, open hotkey, diff highlights), receives the Pulsar asset
    // folder, and points LibGit2Sharp at its native lib. Octokit/LibGit2Sharp are ordinary NuGet
    // deps (separate files, not embedded). All GitHub work lives in ShipyardApi.
    public class Plugin : IPlugin
    {
        public const string Id = "shipyard";

        private Harmony _harmony;

        // Empty under Pulsar's from-source build: the plugin is compiled and loaded IN-MEMORY, so its
        // assembly has no on-disk Location. Return null rather than let Path.GetDirectoryName("") throw
        // "The path is not of a legal form" (which previously took out LogoPath + native resolution).
        public static string PluginDir
        {
            get
            {
                string loc = Assembly.GetExecutingAssembly().Location;
                return string.IsNullOrEmpty(loc) ? null : Path.GetDirectoryName(loc);
            }
        }

        // Pulsar reflectively calls this with the plugin's asset folder when the plugin XML declares an
        // <AssetFolder> (the marketplace/dev-folder path). Brand.LogoPath looks here first; if it's never
        // called (e.g. a bare Local/prebuild install) the logo is found next to the DLL instead.
        public static string AssetDir { get; private set; }
        public void LoadAssets(string assetFolder)
        {
            AssetDir = assetFolder;
            Log("assets folder: " + assetFolder);
        }

        public void Init(object gameInstance)
        {
            Log("Init: loading");
            InitLocalGit();   // point LibGit2Sharp at the native git2 (offline-mode engine)
            try
            {
                _harmony = new Harmony(Id);
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log("Init: Harmony patches applied (pluginDir=" + PluginDir + ")");
            }
            catch (Exception ex) { Log("Init FAILED: " + ex); }
        }

        // LibGit2Sharp's native git2-*.dll must be loadable before the first libgit2 call. Deps ship as
        // separate files: the manual/prebuild build copies the native alongside the plugin; Pulsar's
        // from-source build restores it into Pulsar's NuGet cache. If a valid native path is already set
        // (e.g. by a future Pulsar resolver) we defer to it; otherwise we locate the native and set it.
        // NOTE: a FAILED native load permanently poisons LibGit2Sharp.Core.NativeMethods, so the path must
        // be set BEFORE any libgit2 call — we must not "try to load and catch". Reading NativeLibraryPath
        // does NOT trigger the load (reading Version would). Only OFFLINE (local-git) mode needs this; the
        // online GitHub path (Octokit) does not.
        private const string NativeGitDll = "git2-a418d9d.dll";           // tied to LibGit2Sharp 0.30.0
        private const string NativeBinariesPkg = "LibGit2Sharp.NativeBinaries";
        private const string NativeBinariesVer = "2.0.322";               // transitive of LibGit2Sharp 0.30.0
        private static void InitLocalGit()
        {
            try
            {
                // Defer only when the existing path actually contains the native, so we don't clobber a good
                // resolver but do fix LibGit2Sharp's empty/default path under Pulsar.
                string current = LibGit2Sharp.GlobalSettings.NativeLibraryPath;
                if (!string.IsNullOrEmpty(current) && File.Exists(Path.Combine(current, NativeGitDll))) return;

                string found = FindNativeGitDir();
                if (found != null) { LibGit2Sharp.GlobalSettings.NativeLibraryPath = found; Log("LibGit2Sharp native resolved: " + found); }
                else Log("InitLocalGit: native git2 not found - offline/local-git mode unavailable (online GitHub mode is unaffected)");
            }
            catch (Exception ex) { Log("InitLocalGit failed: " + ex.Message); }
        }

        // Locate the folder holding the native git2-*.dll across distribution modes — cheap direct-path probes
        // first, recursive sweep only as a last resort. Always prefers the process architecture (rid).
        private static string FindNativeGitDir()
        {
            string rid = Environment.Is64BitProcess ? "win-x64" : "win-x86";
            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string entryDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? "");

            // 1. Alongside the plugin (a prebuild ships the native next to Shipyard.dll).
            if (!string.IsNullOrEmpty(PluginDir))
                foreach (var d in new[] { PluginDir, Path.Combine(PluginDir, "runtimes", rid, "native"), Path.Combine(PluginDir, "lib", "win32", arch) })
                    if (File.Exists(Path.Combine(d, NativeGitDll))) return d;

            // 2. NuGet caches: Pulsar's own (from-source build) and the global cache (dev machine).
            var pkgRoots = new[]
            {
                string.IsNullOrEmpty(entryDir) ? null : Path.Combine(entryDir, "Legacy", "NuGet", "packages"),  // portable Pulsar (SE1/Legacy)
                string.IsNullOrEmpty(entryDir) ? null : Path.Combine(entryDir, "NuGet", "packages"),
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
                    if (File.Exists(Path.Combine(dir, NativeGitDll))) return dir;
                }
            }
            // 3. Last resort: bounded recursive sweep of small roots only (never the huge global cache).
            foreach (var root in new[] { PluginDir, entryDir })
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
