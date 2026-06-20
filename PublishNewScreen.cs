using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ShipyardPlugin
{
    // Step 2 of "Publish as NEW": the local blueprint is already chosen (Publish grid). Collect the
    // destination name / folder / tags, then open the PR. Kept as a small modal so the Update tab
    // stays a clean browse grid.
    public class PublishNewScreen : MyGuiScreenDebugBase
    {
        private const string DefaultFolder = "PvP"; // seed folder for new publishes; user can overwrite.
        private readonly string _local;
        private MyGuiControlTextbox _name, _folder, _tags;

        public PublishNewScreen(string localName)
            // top-most so it layers ABOVE the (non-top-most) Shipyard menu instead of opening behind it.
            : base(new Vector2(0.5f, 0.5f), new Vector2(0.62f, 0.5f), Brand.Bg, isTopMostScreen: true)
        {
            _local = localName;
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "PublishNewScreen";

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            float top = -0.25f;

            Center("PUBLISH AS NEW SHIP", top + 0.05f, Brand.Accent, 0.9f);
            Center("FORMIDAN MANDATE", top + 0.082f, Brand.AccentDim, 0.62f);
            Center("from local blueprint:  " + _local, top + 0.12f, new Vector4(0.85f, 0.88f, 0.92f, 1f), 0.8f);

            Field("Name:", -0.05f, out _name, _local);
            Field("Folder:", 0.0f, out _folder, DefaultFolder);
            var fh = new MyGuiControlLabel(new Vector2(0.065f, 0.032f), null, "any path, e.g.  PvP/Frigate  - new folders are created", Brand.Muted, 0.7f)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER };
            Controls.Add(fh);
            Field("Tags:", 0.06f, out _tags, "");
            var th = new MyGuiControlLabel(new Vector2(0.065f, 0.092f), null, "optional, comma-separated  -  e.g.  pvp, frigate, meta", Brand.Muted, 0.7f)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER };
            Controls.Add(th);

            MakeBtn("Publish", new Vector2(-0.14f, 0.18f), new Vector2(0.26f, 0.045f), OnConfirm);
            MakeBtn("Cancel", new Vector2(0.14f, 0.18f), new Vector2(0.26f, 0.045f), () => CloseScreen(false));
        }

        private void Field(string label, float y, out MyGuiControlTextbox box, string initial)
        {
            var lbl = new MyGuiControlLabel(new Vector2(-0.27f, y), null, label, Vector4.One, 0.85f)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER };
            Controls.Add(lbl);
            box = new MyGuiControlTextbox { Position = new Vector2(0.065f, y), Size = new Vector2(0.45f, 0.035f) };
            if (!string.IsNullOrEmpty(initial)) box.Text = initial;
            Controls.Add(box);
        }

        private void OnConfirm()
        {
            string name = (_name != null ? _name.Text : "").Trim();
            string folder = (_folder != null ? _folder.Text : "").Trim();
            if (string.IsNullOrEmpty(name)) name = _local;
            // Validate against the SAME slug rules AddNewShip applies, BEFORE closing the screen — so a name/
            // folder that slugs to empty surfaces a correctable error here instead of after the screen is gone.
            if (string.IsNullOrWhiteSpace(ShipyardApi.SlugPath(folder))) { ShipyardRunner.ShowMessage("Enter a folder (e.g. PvP or PvP/Frigate)."); return; }
            if (string.IsNullOrWhiteSpace(ShipyardApi.SlugPath(name))) { ShipyardRunner.ShowMessage("Enter a ship name first."); return; }
            var tags = (_tags != null ? _tags.Text : "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            CloseScreen(false);
            ShipyardApi.AddNewShip(_local, name, folder, tags);
        }

        private void Center(string text, float y, Vector4 color, float scale)
            => Controls.Add(Frame.CenterLabel(text, y, color, scale));

        private void MakeBtn(string text, Vector2 pos, Vector2 size, Action onClick)
        {
            var b = Frame.MakeButton(text, pos, size, _ => onClick());
            Controls.Add(b);
        }
    }
}
