using System;
using System.Text;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ShipyardPlugin
{
    // Shared "corp console" framing for a unified look across screens: a thin divider rule, the
    // Mandate classification strip, and the two controls every screen builds (fixed-size button,
    // centered label). Controls are returned so each screen adds them to its own list.
    static class Frame
    {
        // Re-apply the configured background (color + opacity) to every Shipyard screen currently open,
        // so a Settings change is visible without closing/reopening. MyGuiScreenBase reads
        // BackgroundColor live each frame, so setting it refreshes the menu in place. Covers the screen
        // behind Settings (the Shipyard menu) as well as Settings itself.
        public static void ApplyThemeToOpenScreens()
        {
            try
            {
                var bg = Brand.Bg;
                // Derive the namespace from this type so a rename stays consistent (a hardcoded
                // literal would silently break with no compiler error). Ideally Shipyard screens
                // would share a marker base type for a compiler-checked test, but they each
                // subclass the game's MyGuiScreenDebugBase directly.
                var ns = typeof(Frame).Namespace;
                foreach (var s in MyScreenManager.Screens)
                    if (s != null && s.GetType().Namespace == ns)
                        s.BackgroundColor = bg;
            }
            catch (Exception ex) { Plugin.Log("ApplyThemeToOpenScreens failed: " + ex.Message); }
        }

        // A fixed-size Rectangular button. (MyGuiScreenDebugBase.AddButton's debug style ignores
        // Size, so anything that must sit beside another control needs an explicit one.)
        public static MyGuiControlButton MakeButton(string text, Vector2 pos, Vector2 size, Action<MyGuiControlButton> onClick)
            => new MyGuiControlButton(position: pos, visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
                size: size, text: new StringBuilder(text), onButtonClick: onClick);

        // Text scale for the classification-strip footer (see MakeFooter). Kept here so the
        // footer's sizing convention is documented and tunable in one place.
        const float FooterScale = 0.6f;

        // A horizontally-centered label at panel-relative height y.
        public static MyGuiControlLabel CenterLabel(string text, float y, Vector4 color, float scale)
            => new MyGuiControlLabel(new Vector2(0f, y), null, text, color, scale)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER };

        // A thin horizontal divider centered on the panel at height y.
        public static MyGuiControlSeparatorList MakeDivider(float y, float width)
        {
            var sep = new MyGuiControlSeparatorList();
            sep.AddHorizontal(new Vector2(-width / 2f, y), width);
            return sep;
        }

        // The classification strip footer (centered).
        public static MyGuiControlLabel MakeFooter(float y)
            => CenterLabel(Brand.Classified, y, Brand.AccentDim, FooterScale);
    }
}
