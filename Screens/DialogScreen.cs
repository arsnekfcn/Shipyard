using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ShipyardPlugin
{
    // In-style Mandate terminal dialog that replaces the stock MyGuiScreenMessageBox for our result
    // (single ACKNOWLEDGE) and confirm (CONFIRM / CANCEL) prompts, so the user never drops out of the
    // terminal aesthetic. Result is delivered via onResult(true = confirm/ack, false = cancel).
    public class DialogScreen : MyGuiScreenDebugBase
    {
        private readonly string _title;
        private readonly string[] _lines;
        private readonly bool _confirm;
        private readonly Action<bool> _onResult;
        private readonly Action _onDontShowAgain;   // null = no "don't show again" checkbox
        private readonly Vector2 _size;
        private bool _answered;
        private MyGuiControlLabel _slogan;
        private MyGuiControlCheckbox _dontCheck;
        private int _tick, _sloganIdx;

        public DialogScreen(string title, string body, bool confirm, Action<bool> onResult, Action onDontShowAgain = null)
            : this(title, Wrap(body), confirm, onResult, onDontShowAgain)
        {
        }

        // Private ctor takes the already-wrapped lines so Wrap/Measure each run exactly once: the wrapped
        // lines are computed by the delegating ctor above and reused here for the base size, _size and _lines.
        private DialogScreen(string title, string[] lines, bool confirm, Action<bool> onResult, Action onDontShowAgain)
            : base(new Vector2(0.5f, 0.5f), Measure(lines, onDontShowAgain != null), Brand.Bg, isTopMostScreen: true)
        {
            _title = string.IsNullOrEmpty(title) ? "SHIPYARD" : title.ToUpperInvariant();
            _lines = lines;
            _confirm = confirm;
            _onResult = onResult;
            _onDontShowAgain = onDontShowAgain;
            _size = Measure(lines, onDontShowAgain != null);
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "ShipyardDialog";

        // Word-wrap width in characters; used in both the fast-path and the per-word accumulation below.
        private const int WrapWidth = 64;
        private const float LineHeight = 0.035f;   // vertical advance per wrapped body line
        private const float BaseHeight = 0.28f;    // fixed chrome (title/slogan/button) height
        private const float CheckboxRowHeight = 0.05f;
        private const float MaxHeight = 0.85f;

        // ~64-char word wrap that honors explicit newlines.
        private static string[] Wrap(string body)
        {
            var outLines = new List<string>();
            foreach (var raw in (body ?? "").Replace("\r", "").Split('\n'))
            {
                if (raw.Length <= WrapWidth) { outLines.Add(raw); continue; }
                var sb = new StringBuilder();
                foreach (var word in raw.Split(' '))
                {
                    if (sb.Length > 0 && sb.Length + word.Length + 1 > WrapWidth) { outLines.Add(sb.ToString()); sb.Clear(); }
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(word);
                }
                if (sb.Length > 0) outLines.Add(sb.ToString());
            }
            return outLines.ToArray();
        }

        private static Vector2 Measure(string[] lines, bool dontShowRow = false)
        {
            int n = Math.Max(1, lines.Length);
            float h = BaseHeight + n * LineHeight + (dontShowRow ? CheckboxRowHeight : 0f);   // + room for the checkbox row
            if (h > MaxHeight) h = MaxHeight;
            return new Vector2(0.7f, h);
        }

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            float top = -_size.Y / 2f;

            Center(_title, top + 0.05f, Brand.Accent, 0.9f);
            Center("FORMIDAN MANDATE", top + 0.082f, Brand.AccentDim, 0.62f);

            float y = top + 0.135f;
            foreach (var line in _lines)
            {
                Center(string.IsNullOrEmpty(line) ? " " : line, y, new Vector4(0.85f, 0.88f, 0.92f, 1f), 0.78f);
                y += LineHeight;
            }

            // Optional "don't show this again" checkbox between the body and the button.
            if (_onDontShowAgain != null)
            {
                _dontCheck = new MyGuiControlCheckbox(new Vector2(-0.105f, y + 0.012f)) { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER };
                Controls.Add(_dontCheck);
                Controls.Add(new MyGuiControlLabel(new Vector2(-0.075f, y + 0.012f), null, "Don't show this again", Brand.Muted, 0.72f)
                { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER });
                y += 0.045f;
            }

            // Button sits BELOW the body (top-relative) so a short message never ends up behind it.
            float by = y + 0.045f;
            if (_confirm)
            {
                MakeBtn("CONFIRM", new Vector2(-0.13f, by), new Vector2(0.24f, 0.045f), () => Answer(true));
                MakeBtn("CANCEL", new Vector2(0.13f, by), new Vector2(0.24f, 0.045f), () => Answer(false));
            }
            else
            {
                MakeBtn("ACKNOWLEDGE", new Vector2(0f, by), new Vector2(0.3f, 0.045f), () => Answer(true));
            }

            _sloganIdx = Brand.RandomSloganIndex();
            _slogan = new MyGuiControlLabel(new Vector2(0f, by + 0.05f), null, "\"" + Brand.Slogans[_sloganIdx] + "\"", Brand.AccentDim, 0.62f)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER };
            Controls.Add(_slogan);
        }

        public override bool Update(bool hasFocus)
        {
            _tick++;
            if (_slogan != null && _tick % 200 == 0)
            {
                _sloganIdx = Brand.NextSloganIndex(_sloganIdx);
                _slogan.Text = "\"" + Brand.Slogans[_sloganIdx] + "\"";
            }
            return base.Update(hasFocus);
        }

        // Close first, then fire the callback, which may itself open another screen.
        void Answer(bool ok)
        {
            if (_answered) return;
            _answered = true;
            bool dontShow = _dontCheck != null && _dontCheck.IsChecked;
            CloseScreen(false);
            if (ok && dontShow) { try { _onDontShowAgain?.Invoke(); } catch (Exception ex) { Plugin.Log("dontShow callback failed: " + ex.Message); } }
            try { _onResult?.Invoke(ok); } catch (Exception ex) { Plugin.Log("dialog result failed: " + ex.Message); }
        }

        // Esc / any external close bypasses Answer(), which would silently drop the cancel callback.
        // Honour the contract here: fire _onResult(false) once. Guarded by _answered so a button-click
        // (which sets it before CloseScreen) doesn't double-fire; we do NOT call CloseScreen here (the
        // screen is already closing) to avoid re-entry.
        protected override void OnClosed()
        {
            if (!_answered)
            {
                _answered = true;
                try { _onResult?.Invoke(false); } catch (Exception ex) { Plugin.Log("dialog cancel failed: " + ex.Message); }
            }
            base.OnClosed();
        }

        void Center(string text, float yy, Vector4 color, float scale)
            => Controls.Add(Frame.CenterLabel(text, yy, color, scale));

        MyGuiControlButton MakeBtn(string text, Vector2 pos, Vector2 size, Action onClick)
        {
            var b = Frame.MakeButton(text, pos, size, _ => onClick());
            Controls.Add(b);
            return b;
        }
    }
}
