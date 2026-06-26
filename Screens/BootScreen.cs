using System;
using System.Text;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ShipyardPlugin
{
    // Formidan Mandate boot sequence shown when the Shipyard opens. The typewriter "self-check" plays WHILE
    // the repo is fetched in the background; once both the animation finishes AND the data is in, it
    // hands off to the main ShipyardScreen (or shows the error). Animated via the per-frame Update hook. 
    // Only runs once per session to handle the initial handshake and flag any errors before user runs Shipyard operations.
    public class BootScreen : MyGuiScreenDebugBase
    {
        private static readonly string[] Steps =
        {
            "establishing uplink to MANDATE registry",
            "verifying operator credentials",
            "mounting asset registry",
            "syncing fleet manifest",
            "running integrity check",
        };
        const int TicksPerStep = 14;
        const int SpinTicks = 4;      // spinner char advances / status refreshes every Nth tick
        const int TailHoldTicks = 20; // extra ticks held after the last step before anim is "done"
        const int DotColumns = 40;    // dotted-leader column width for step labels

        private readonly string _localName;
        private readonly ShipyardScreen.Tab _openTab;
        private readonly string _openFolder;
        private MyGuiControlLabel[] _stepLabels;
        private MyGuiControlLabel _status;
        private int _tick;
        private bool _proceeded;

        // Set via Finish(), which ShipyardApi always invokes on the main thread (InvokeOnMain),
        // same thread as Update(). All access is single-threaded, so no volatile is needed.
        private bool _hasResult;
        private ShipyardData _data;
        private string _err;
        private bool _closed;

        // Panel size, single source (RecreateControls lays out relative to these).
        const float PanelW = 0.72f, PanelH = 0.5f;

        public BootScreen(string localName, ShipyardScreen.Tab openTab = ShipyardScreen.Tab.Browse, string openFolder = null)
            : base(new Vector2(0.5f, 0.5f), new Vector2(PanelW, PanelH), Brand.Bg, isTopMostScreen: true)
        {
            _localName = localName;
            _openTab = openTab;
            _openFolder = openFolder;
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "ShipyardBoot";

        // Called (on the main thread) when the background fetch completes.
        // Early-out if the user already hit EXIT during a slow fetch: the screen is gone, so
        // mutating its fields (and the periodic InvokeOnMain work) would just be wasted.
        public void Finish(ShipyardData data, string err) { if (_closed) return; _data = data; _err = err; _hasResult = true; }

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            float top = -PanelH / 2f;

            CenterLine(Brand.Faction, top + 0.05f, Brand.Accent, 0.95f);
            CenterLine(Brand.Product + "    " + Brand.Version, top + 0.085f, Brand.AccentDim, 0.7f);

            _stepLabels = new MyGuiControlLabel[Steps.Length];
            float y = top + 0.155f;
            for (int i = 0; i < Steps.Length; i++)
            {
                _stepLabels[i] = new MyGuiControlLabel(new Vector2(-0.32f, y), null, "", Brand.Accent, 0.8f)
                { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER };
                Controls.Add(_stepLabels[i]);
                y += 0.04f;
            }

            _status = CenterLine("", y + 0.03f, Brand.Accent, 1.0f);

            // Emergency exit so the boot sequence can never trap the player (e.g. a hung fetch).
            Controls.Add(Frame.MakeButton("EXIT", new Vector2(0f, PanelH / 2f - 0.05f),
                new Vector2(0.18f, 0.04f), _ => { _closed = true; CloseScreen(false); }));
        }

        public override bool Update(bool hasFocus)
        {
            _tick++;
            // The spinner char only changes every SpinTicks-th tick.
            char spin = "|/-\\"[(_tick / SpinTicks) % 4];
            if (_tick == 1 || _tick % SpinTicks == 0)
            {
                int target = Math.Min(Steps.Length, _tick / TicksPerStep);
                for (int i = 0; i < Steps.Length; i++)
                {
                    if (_stepLabels == null) break;
                    if (i < target) { _stepLabels[i].Text = "> " + Steps[i].PadRight(DotColumns, '.') + " OK"; _stepLabels[i].ColorMask = Brand.Ok; }
                    else if (i == target) { _stepLabels[i].Text = "> " + Steps[i].PadRight(DotColumns, '.') + " " + spin; _stepLabels[i].ColorMask = Brand.Accent; }
                    else _stepLabels[i].Text = "";
                }
            }

            bool animDone = _tick >= Steps.Length * TicksPerStep + TailHoldTicks;
            if (_status != null && _tick % SpinTicks == 0 && animDone)
            {
                if (!_hasResult) _status.Text = "AWAITING REGISTRY " + spin;
                else if (_err != null) { _status.Text = "ACCESS DENIED"; _status.ColorMask = Brand.Warn; }
                else _status.Text = "ACCESS GRANTED";
            }

            if (animDone && _hasResult && !_proceeded)
            {
                _proceeded = true;
                // Ensure the terminal status is shown even on the tick it first becomes available
                // (the periodic SpinTicks-gated refresh above may not fire on this exact tick).
                // On the error path this is the only chance the user sees "ACCESS DENIED" before
                // OpenSettings()+ShowMessage take over.
                if (_status != null)
                {
                    if (_err != null) { _status.Text = "ACCESS DENIED"; _status.ColorMask = Brand.Warn; }
                    else _status.Text = "ACCESS GRANTED";
                }
                var d = _data; var e = _err; var ln = _localName; var tb = _openTab; var fl = _openFolder;
                // defer the close+open one frame (don't mutate the screen list mid-Update enumeration)
                ShipyardRunner.InvokeOnMain(() =>
                {
                    _closed = true;
                    CloseScreen(false);
                    // A failed open is almost always a config/access problem (wrong repo, no invite,
                    // expired token). No dead-end for the user, we place the Auth / Repo
                    // screen (under the box) so closing it lands them where they fix it.
                    // Alternate escape would be /sy auth.
                    if (e != null) { ShipyardApi.OpenSettings(); ShipyardRunner.ShowMessage(e); }
                    else MyGuiSandbox.AddScreen(new ShipyardScreen(d.Ships, d.Prs, ln, tb, fl));
                });
            }
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
