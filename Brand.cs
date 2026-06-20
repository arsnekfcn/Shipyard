using System;
using System.IO;
using VRageMath;

namespace ShipyardPlugin
{
    // Cosmetic skin: the tool is functionally "Shipyard", presented as a Formidan Mandate proprietary
    // module. All flavor strings + the palette live here so the lore is easy to tweak in one place.
    internal static class Brand
    {
        public const string Faction = "FORMIDAN MANDATE";
        public const string Product = "SHIPYARD CONTROL MODULE";
        public const string Version = "v0.9-beta";
        public const string Slogan  = "Compliance is efficiency.";
        public const string Classified =
            "PROPRIETARY  ||  PROPERTY OF THE FORMIDAN MANDATE  ||  UNAUTHORIZED USE IS A LIABILITY EVENT";

        // Rotating corp slogans shown on terminal/loading screens.
        public static readonly string[] Slogans =
        {
            "Compliance is efficiency.",
            "Your ships. Our standards.",
            "Asset integrity is non-negotiable.",
            "Build. Submit. Comply.",
            "Deviation is documented.",
            "The Mandate provides.",
            "Productivity is loyalty.",
            "Every hull accounted for.",
            "Unauthorized creativity will be reviewed.",
            "Fabrication within parameters.",
            "Order through engineering.",
            "Peace through superior firepower",
            "Requisition acknowledged.",
        };

        // Slogan rotation is randomized so loading screens don't always open on the same line.
        // System.Random is not thread-safe; guard every access with a lock so an off-thread caller
        // can't corrupt its internal state (callers today are main-thread GUI screens, so this is cheap).
        private static readonly Random _rng = new Random();
        public static int RandomSloganIndex() { lock (_rng) { return _rng.Next(Slogans.Length); } }
        // A random index different from 'current', so a rotation never repeats the same line back-to-back.
        public static int NextSloganIndex(int current)
        {
            if (Slogans.Length <= 1) return 0;
            int i;
            lock (_rng) { do { i = _rng.Next(Slogans.Length); } while (i == current); }
            return i;
        }

        // Amber/steel megacorp palette.
        public static readonly Vector4 Accent    = new Vector4(0.95f, 0.62f, 0.12f, 1f);  // amber/gold
        public static readonly Vector4 AccentDim = new Vector4(0.70f, 0.50f, 0.22f, 1f);
        public static readonly Vector4 Warn      = new Vector4(0.95f, 0.32f, 0.22f, 1f);  // alert red
        public static readonly Vector4 Ok        = new Vector4(0.55f, 0.95f, 0.55f, 1f);
        // Panel background for ALL Shipyard screens. Read LIVE from per-user config (color + opacity,
        // set in Settings) so a newly-opened menu reflects the current choice. Falls back to the steel default.
        public static Vector4 Bg
        {
            get { var c = HexRgb(Auth.BgColorHex); return new Vector4(c.X, c.Y, c.Z, Auth.BgAlpha); }
        }
        // Steel-panel fallback, kept in sync with Auth.DefaultBgColor ("090A0D") by parsing the same
        // canonical hex string here rather than hard-coding magic floats that could silently diverge.
        private static readonly Vector3 SteelDefault = ParseHex("090A0D");
        private static Vector3 HexRgb(string hex)
        {
            try { return ParseHex(hex); }
            // Unreachable on the real path: Auth.BgColorHex is already a normalized 6-char hex string
            // (Auth.NormalizeHex enforces ^[0-9A-F]{6}$, falling back to DefaultBgColor). Defensive only,
            // so no Plugin.Log here unlike LogoPath.
            catch { return SteelDefault; }
        }
        private static Vector3 ParseHex(string hex) => new Vector3(
            Convert.ToInt32(hex.Substring(0, 2), 16) / 255f,
            Convert.ToInt32(hex.Substring(2, 2), 16) / 255f,
            Convert.ToInt32(hex.Substring(4, 2), 16) / 255f);
        public static readonly Vector4 Muted     = new Vector4(0.62f, 0.62f, 0.66f, 1f);

        public static string LogoPath()
        {
            try
            {
                string dir = Plugin.PluginDir;
                string a = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "logo.png");
                string b = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shipyard", "logo.png");
                if (a != null && File.Exists(a)) return a;
                if (File.Exists(b)) return b;
            }
            catch (Exception ex) { Plugin.Log("LogoPath failed: " + ex.Message); }
            return null;
        }
    }
}
