using System;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ShipyardPlugin
{
    // General settings (display / diff-overlay). Checkboxes (xray, per-category show) apply live;
    // the count / distance / color textboxes apply on Save. Reached from the Browse "Settings" button
    // or /sy filters. Diff prefs persist per-user in config.
    public class HighlightOptionsScreen : MyGuiScreenDebugBase
    {
        // (category key, display name)
        private static readonly string[][] Cats =
        {
            new[] { "added", "Added" }, new[] { "removed", "Removed" }, new[] { "changed", "Changed" },
            new[] { "recolored", "Recolored" }, new[] { "data", "Data / inventory" },
        };

        private MyGuiControlTextbox _count, _dist, _bgHex, _bgOpacity;
        private MyGuiControlTextbox[] _hex = new MyGuiControlTextbox[5];

        public HighlightOptionsScreen()
            : base(new Vector2(0.5f, 0.5f), new Vector2(0.62f, 0.90f), Brand.Bg, isTopMostScreen: true)
        {
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "ShipyardSettings";

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            Center("SETTINGS", -0.40f, Brand.Accent, 0.9f);
            Center("Display, diff overlay, and menu appearance.", -0.368f, Brand.Muted, 0.66f);
            Controls.Add(Frame.MakeDivider(-0.34f, 0.56f));

            // xray + numeric prefs
            Check("Draw highlights through blocks  (xray)", Auth.XrayHighlights, -0.30f, v => Auth.SetXray(v));

            Label("Max highlights shown:", -0.30f, -0.255f);
            _count = Box(0.07f, -0.255f, Auth.HighlightCount.ToString(), 0.12f);
            Label("Highlight distance (m, 0 = all):", -0.30f, -0.21f);
            _dist = Box(0.07f, -0.21f, Auth.HighlightDistance.ToString(), 0.12f);

            // Menu appearance: background color + opacity, applied to ALL Shipyard screens.
            Label("Menu background (hex):", -0.30f, -0.165f);
            _bgHex = Box(0.07f, -0.165f, Auth.BgColorHex, 0.13f);
            Label("Menu opacity (0-100):", -0.30f, -0.12f);
            _bgOpacity = Box(0.07f, -0.12f, ((int)Math.Round(Auth.BgAlpha * 100f)).ToString(), 0.12f);

            Controls.Add(Frame.MakeDivider(-0.085f, 0.56f));
            Center("Show / color (RRGGBB hex - for color-blindness)", -0.06f, Brand.Muted, 0.66f);

            float y = -0.015f;
            for (int i = 0; i < Cats.Length; i++)
            {
                string cat = Cats[i][0];
                Check(Cats[i][1], Auth.IsDiffShown(cat), y, v => Auth.SetDiffShown(cat, v), checkX: -0.27f, labelX: -0.235f);
                _hex[i] = Box(0.12f, y, Auth.DiffColorHex(cat), 0.13f);
                y += 0.05f;
            }

            MakeBtn("Save  (applies to all menus)", new Vector2(0f, y + 0.02f), new Vector2(0.46f, 0.045f), OnSave);
            MakeBtn("Clear all highlights", new Vector2(-0.13f, y + 0.08f), new Vector2(0.24f, 0.04f),
                () => { HighlightManager.Clear(); CloseScreen(false); });
            MakeBtn("Close", new Vector2(0.13f, y + 0.08f), new Vector2(0.24f, 0.04f), () => CloseScreen(false));

            Center(Brand.Classified, 0.41f, Brand.AccentDim, 0.58f);
        }

        private void OnSave()
        {
            // Malformed count/dist/opacity (TryParse fails) keep the stored value; RecreateControls(false)
            // below snaps the textbox back to it. Silent by design - bad input is simply discarded.
            // Batch all the setters into ONE config write (each Auth.SetXxx would otherwise Save() separately).
            Auth.BeginBatch();
            try
            {
                int n; if (int.TryParse((_count.Text ?? "").Trim(), out n)) Auth.SetHighlightCount(n);
                int d; if (int.TryParse((_dist.Text ?? "").Trim(), out d)) Auth.SetHighlightDistance(d);
                for (int i = 0; i < Cats.Length; i++) Auth.SetDiffColorHex(Cats[i][0], _hex[i].Text);
                Auth.SetBgColorHex(_bgHex.Text);
                int op; if (int.TryParse((_bgOpacity.Text ?? "").Trim(), out op)) Auth.SetBgAlpha(op / 100f);   // SetBgAlpha clamps to 0..1
            }
            finally { Auth.EndBatch(); }   // single write, even if a setter throws
            // Apply the new background to ALL open Shipyard menus right now (this screen + the menu behind
            // it), no close/reopen needed. Then refresh this screen's textboxes to show clamped values.
            Frame.ApplyThemeToOpenScreens();
            RecreateControls(false);
        }

        private void Check(string label, bool init, float y, Action<bool> set, float checkX = -0.25f, float labelX = -0.215f)
        {
            var cb = new MyGuiControlCheckbox(new Vector2(checkX, y))
            { IsChecked = init, OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER };
            cb.IsCheckedChanged += c => set(c.IsChecked);
            Controls.Add(cb);
            Controls.Add(new MyGuiControlLabel(new Vector2(labelX, y), null, label)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER });
        }

        private void Label(string text, float x, float y)
            => Controls.Add(new MyGuiControlLabel(new Vector2(x, y), null, text)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER });

        private MyGuiControlTextbox Box(float x, float y, string text, float w)
        {
            var b = new MyGuiControlTextbox { Position = new Vector2(x, y), Size = new Vector2(w, 0.035f) };
            if (!string.IsNullOrEmpty(text)) b.Text = text;
            Controls.Add(b);
            return b;
        }

        private void Center(string text, float y, Vector4 color, float scale)
            => Controls.Add(Frame.CenterLabel(text, y, color, scale));

        private void MakeBtn(string text, Vector2 pos, Vector2 size, Action onClick)
            => Controls.Add(Frame.MakeButton(text, pos, size, _ => onClick()));
    }
}
