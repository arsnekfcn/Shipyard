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
            // Load bundled deps (Octokit.dll, LibGit2Sharp.dll, etc.) from our embedded resources
            AppDomain.CurrentDomain.AssemblyResolve += ResolveSibling;
            InitLocalGit();   // extract the native libgit2 + point LibGit2Sharp at it (offline mode engine)
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
        // pointed at its folder. Two build models supply it differently:
        //   * LOCAL/manual build: it's EMBEDDED in Shipyard.dll -> extract to %APPDATA%\Shipyard\native.
        //   * PULSAR build: Pulsar ignores our EmbeddedResource (compiles from source) and instead restores
        //     LibGit2Sharp.NativeBinaries via NuGet, but does NOT copy the native dll next to the plugin ->
        //     locate it in Pulsar's NuGet packages folder (see FindNativeGitDir).
        // Either way, if the native dll can't be found, only OFFLINE (local-git) mode is affected; the
        // online GitHub path (Octokit) doesn't need it.
        private const string NativeGitDll = "git2-a418d9d.dll";   // tied to LibGit2Sharp 0.30.0
        private static void InitLocalGit()
        {
            try
            {
                string nativeDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shipyard", "native");
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(NativeGitDll))
                {
                    if (s != null)   // LOCAL/manual build: embedded -> extract
                    {
                        Directory.CreateDirectory(nativeDir);
                        string dest = Path.Combine(nativeDir, NativeGitDll);
                        byte[] buf = ReadAll(s);
                        // size-compare so we only rewrite on first run / version bump (the dll may be loaded/locked)
                        if (!File.Exists(dest) || new FileInfo(dest).Length != buf.Length)
                            try { File.WriteAllBytes(dest, buf); } catch (Exception ex) { Log("native write skipped: " + ex.Message); }
                        LibGit2Sharp.GlobalSettings.NativeLibraryPath = nativeDir;
                        Log("LibGit2Sharp ready (embedded): libgit2 " + LibGit2Sharp.GlobalSettings.Version);
                        return;
                    }
                }
                // PULSAR build: no embedded native dll - find it in Pulsar's restored NuGet packages.
                string found = FindNativeGitDir();
                if (found != null)
                {
                    LibGit2Sharp.GlobalSettings.NativeLibraryPath = found;
                    Log("LibGit2Sharp ready (resolved native): " + found + " -> libgit2 " + LibGit2Sharp.GlobalSettings.Version);
                }
                else
                    Log("InitLocalGit: native git2 not found - offline/local-git mode unavailable (online GitHub mode is unaffected)");
            }
            catch (Exception ex) { Log("InitLocalGit failed: " + ex.Message); }
        }

        // Locate the folder holding a native git2-*.dll (LibGit2Sharp.NativeBinaries) for the Pulsar build,
        // where it lives under .../runtimes/<rid>/native in Pulsar's NuGet packages cache rather than being
        // embedded. Probes the narrow NuGet cache first, then broader fallbacks; returns the first match's
        // directory, or null. Best-effort and one-time (Init); each root is guarded so an inaccessible
        // subtree just moves on to the next root.
        private static string FindNativeGitDir()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string entryDir = null;
            try { entryDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? ""); } catch { }
            var roots = new[]
            {
                Path.Combine(appData, "Pulsar", "NuGet", "packages"),   // documented Pulsar NuGet cache (narrow)
                PluginDir,                                              // next to the compiled plugin
                entryDir,                                               // next to the host (Legacy/Interim) exe
                Path.Combine(appData, "Pulsar"),                        // broad fallback
            };
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, "git2-*.dll", SearchOption.AllDirectories))
                        return Path.GetDirectoryName(f);
                }
                catch (Exception ex) { Log("native search in " + root + " skipped: " + ex.Message); }
            }
            return null;
        }

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
