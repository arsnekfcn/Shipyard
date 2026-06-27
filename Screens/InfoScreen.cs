using System;
using System.Collections.Generic;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace ShipyardPlugin
{
    // A roomy, scrollable, left-aligned text screen for multi-line content (e.g. /sy help, PR details)
    // that a fixed-width message box mangles. Lines go into a MyGuiControlList so long text scrolls.
    public class InfoScreen : MyGuiScreenDebugBase
    {
        private readonly string _title;
        private readonly string[] _lines;

        public InfoScreen(string title, string body)
            : base(new Vector2(0.5f, 0.5f), new Vector2(0.86f, 0.7f),
                   Brand.Bg, isTopMostScreen: true)
        {
            _title = title;
            _lines = (body ?? "").Replace("\r", "").Split('\n');
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "InfoScreen";

        public override void HandleInput(bool receivedFocusInThisUpdate)
        {
            base.HandleInput(receivedFocusInThisUpdate);
            if (MyInput.Static.IsNewKeyPressed(MyKeys.Escape)) CloseScreen(false);
        }

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            AddCaption(_title, Brand.Accent, null, 0.95f);

            // List top must clear the caption (~-0.31): center 0.005, half-height 0.26 -> top -0.255.
            var list = new MyGuiControlList(new Vector2(0f, 0.005f), new Vector2(0.82f, 0.52f),
                null, null, MyGuiControlListStyleEnum.Default);
            var items = new List<MyGuiControlBase>(_lines.Length);
            foreach (var line in _lines)
            {
                var lbl = new MyGuiControlLabel(new Vector2(-0.40f, 0f), null,
                    string.IsNullOrEmpty(line) ? " " : line, Brand.Muted, 0.74f)
                {
                    OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER
                };
                items.Add(lbl);
            }
            list.InitControls(items);
            Controls.Add(list);

            var close = AddButton("Close", (Action<MyGuiControlButton>)(b => CloseScreen(false)), null, null);
            close.Position = new Vector2(0f, 0.285f);   // clear of the footer strip

            Controls.Add(Frame.MakeFooter(0.345f));
        }
    }
}
