using System;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ShipyardPlugin
{
    // One-click new-shipyard setup: creates a PRIVATE GitHub repo on the signed-in account, fully seeded
    // with the shipyard structure, and makes it active - so a new user never has to use GitHub's website.
    // Reached from the Account screen when signed in but no repo is configured yet.
    public class CreateRepoScreen : MyGuiScreenDebugBase
    {
        private MyGuiControlTextbox _name, _root;

        public CreateRepoScreen()
            : base(new Vector2(0.5f, 0.5f), new Vector2(0.74f, 0.6f), Brand.Bg, isTopMostScreen: false)
        {
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "ShipyardCreateRepo";

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);

            Center(Brand.Faction, -0.255f, Brand.Accent, 0.95f);
            Center("CREATE A SHIPYARD  //  NEW GITHUB REPO", -0.22f, Brand.AccentDim, 0.8f);
            Center("Makes a PRIVATE repo on your account, fully set up. Flip it public later if you like.",
                -0.185f, Brand.Muted, 0.72f);

            string acct = string.IsNullOrEmpty(Auth.Login) ? "(signed-in account)" : "@" + Auth.Login;
            Center("Owner:  " + acct, -0.13f, Brand.Ok, 0.78f);

            AddRowLabel("Repo name:", -0.075f);
            _name = AddRowBox(-0.075f, "Shipyard");
            Center("The new repository's name on GitHub.", -0.04f, Brand.Muted, 0.66f);

            AddRowLabel("Top folder:", 0.015f);
            _root = AddRowBox(0.015f, Auth.RootFolder);
            Center("Groups all ships in the repo, e.g. 'Fleet' or your faction tag.", 0.05f, Brand.Muted, 0.66f);

            MakeBtn("Create my Shipyard", new Vector2(0f, 0.12f), new Vector2(0.5f, 0.05f), OnCreate);
            MakeBtn("< Back to Account / Repo", new Vector2(0f, 0.185f), new Vector2(0.5f, 0.045f),
                () => { CloseScreen(false); MyGuiSandbox.AddScreen(new SettingsScreen()); });

            Center(Brand.Classified, 0.255f, Brand.AccentDim, 0.6f);
        }

        private void OnCreate()
        {
            string name = (_name.Text ?? "").Trim();
            string root = (_root.Text ?? "").Trim();
            if (name.Length == 0) { ShipyardRunner.ShowMessage("Enter a name for your shipyard repo."); return; }
            CloseScreen(false);
            ShipyardApi.CreateShipyardRepo(name, root, () => ShipyardApi.OpenShipyard(null));
        }

        private void Center(string text, float y, Vector4 color, float scale)
            => Controls.Add(Frame.CenterLabel(text, y, color, scale));

        private void AddRowLabel(string text, float y)
            => Controls.Add(new MyGuiControlLabel(new Vector2(-0.33f, y), null, text)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER });

        private MyGuiControlTextbox AddRowBox(float y, string text)
        {
            var box = new MyGuiControlTextbox { Position = new Vector2(0.06f, y), Size = new Vector2(0.48f, 0.035f) };
            if (!string.IsNullOrEmpty(text)) box.Text = text;
            Controls.Add(box);
            return box;
        }

        private void MakeBtn(string text, Vector2 pos, Vector2 size, Action onClick)
            => Controls.Add(Frame.MakeButton(text, pos, size, _ => onClick()));
    }
}
