using System;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRageMath;

namespace ShipyardPlugin
{
    // An in-style animated "Mandate terminal" overlay used for busy/loading and sign-in, replacing the stock message boxes.
    public class TerminalScreen : MyGuiScreenDebugBase
    {
        private readonly string _title;
        private readonly string _spinnerMsg;
        private readonly string[] _lines;
        private readonly string _big;          // large emphasis line (e.g. the device code), or null
        private readonly Vector2 _size;

        private MyGuiControlLabel _spinner;
        private MyGuiControlLabel _slogan;
        private int _tick;
        private int _sloganIdx;
        private bool _autoCopied;   // device code copied to clipboard once (not on every relayout)

        // Animation cadence (in ticks). SweepTicks is used in two paired places (% and /) and must stay in sync.
        private const int SweepTicks = 3;
        private const int CaretTicks = 25;
        private const int SloganTicks = 200;

        private static readonly string[] Sweep =
        {
            "[>         ]", "[=>        ]", "[==>       ]", "[===>      ]", "[====>     ]",
            "[=====>    ]", "[======>   ]", "[=======>  ]", "[========> ]", "[=========>]",
            "[ ========>]", "[  =======>]", "[   ======>]", "[    =====>]", "[     ====>]",
            "[      ===>]", "[       ==>]", "[        =>]", "[         >]", "[          ]",
        };

        public TerminalScreen(string title, string spinnerMsg, string body, string big, Vector2 size)
            : base(new Vector2(0.5f, 0.5f), size, Brand.Bg, isTopMostScreen: true)
        {
            _title = title;
            _spinnerMsg = spinnerMsg ?? "";
            _lines = (body ?? "").Replace("\r", "").Split('\n');
            _big = big;
            _size = size;
            _sloganIdx = Brand.RandomSloganIndex();
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "ShipyardTerminal";

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            float top = -_size.Y / 2f;
            float bottom = _size.Y / 2f;

            CenterLine(_title, top + 0.05f, Brand.Accent, 0.95f);
            CenterLine("FORMIDAN MANDATE", top + 0.085f, Brand.AccentDim, 0.65f);

            float y = top + 0.15f;
            foreach (var line in _lines)
            {
                CenterLine(string.IsNullOrEmpty(line) ? " " : line, y, new Vector4(0.85f, 0.88f, 0.92f, 1f), 0.8f);
                y += 0.04f;
            }
            if (!string.IsNullOrEmpty(_big))
            {
                CenterLine(_big, y + 0.015f, Brand.Accent, 1.4f);
                y += 0.06f;
                // Auto-copy the device code so the player can paste it straight into GitHub. ONCE only -
                // RecreateControls can re-run on relayout, and re-copying would clobber the user's clipboard.
                if (!_autoCopied)
                {
                    _autoCopied = true;
                    // Clipboard copy is a convenience; if it fails the code is still shown on-screen to copy by hand.
                    try { VRage.Utils.MyClipboardHelper.SetClipboard(_big); } catch { }
                }
                CenterLine("(copied to clipboard - paste it on the GitHub page)", y, Brand.AccentDim, 0.6f);
                y += 0.03f;
            }

            // Keep the sweep bar above the EXIT button (at bottom-0.095) no matter how short the box is.
            float spinnerY = Math.Min(y + 0.03f, bottom - 0.155f);
            _spinner = CenterLine(Sweep[0] + "   " + _spinnerMsg, spinnerY, Brand.Accent, 0.9f);
            _slogan = CenterLine("\"" + Brand.Slogans[_sloganIdx] + "\"", bottom - 0.045f, Brand.AccentDim, 0.7f);

            // Emergency exit: never trap the player behind a busy/sign-in overlay. The background work
            // (if any) keeps running and still posts its result; this just dismisses the overlay.
            // With a device code present, also offer a manual re-copy next to EXIT.
            if (!string.IsNullOrEmpty(_big))
            {
                Controls.Add(Frame.MakeButton("COPY CODE", new Vector2(-0.12f, bottom - 0.095f),
                    new Vector2(0.18f, 0.04f), _ => { try { VRage.Utils.MyClipboardHelper.SetClipboard(_big); } catch { /* convenience copy; code is on-screen to copy by hand */ } }));
                Controls.Add(Frame.MakeButton("EXIT", new Vector2(0.12f, bottom - 0.095f),
                    new Vector2(0.18f, 0.04f), _ => CloseScreen(false)));
            }
            else
                Controls.Add(Frame.MakeButton("EXIT", new Vector2(0f, bottom - 0.095f),
                    new Vector2(0.18f, 0.04f), _ => CloseScreen(false)));
        }

        public override bool Update(bool hasFocus)
        {
            // Catch-all so a per-frame hook can never throw into the sim loop (matches BootScreen convention).
            try
            {
                _tick++;
                // The bar only advances every SweepTicks-th tick — skip the string rebuild on the frames between.
                if (_spinner != null && _tick % SweepTicks == 0)
                {
                    string bar = Sweep[(_tick / SweepTicks) % Sweep.Length];
                    bool caret = (_tick / CaretTicks) % 2 == 0;
                    _spinner.Text = bar + "   " + _spinnerMsg + (caret ? " _" : "  ");
                }
                if (_slogan != null && _tick % SloganTicks == 0)
                {
                    _sloganIdx = Brand.NextSloganIndex(_sloganIdx);
                    _slogan.Text = "\"" + Brand.Slogans[_sloganIdx] + "\"";
                }
            }
            catch { }   // per-frame overlay animation: a transient draw/state error must not break Update, and logging every frame would spam
            return base.Update(hasFocus);
        }

        MyGuiControlLabel CenterLine(string text, float y, Vector4 color, float scale)
        {
            var lbl = Frame.CenterLabel(text, y, color, scale);
            Controls.Add(lbl);
            return lbl;
        }
    }
}
