using System;
using System.IO;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ShipyardPlugin
{
    // Offline setup: choose the local shipyard folder, top folder, and author handle, then create/adopt
    // the local git repo. Also the offline "Account" screen (reachable from the Browse tab), so it can
    // switch back to Online. The plugin never manages a remote. It's a normal git repo the user can
    // host themselves with their own tooling.
    public class OfflineSetupScreen : MyGuiScreenDebugBase
    {
        private MyGuiControlTextbox _path, _root, _author;

        public OfflineSetupScreen()
            : base(new Vector2(0.5f, 0.5f), new Vector2(0.74f, 0.66f), Brand.Bg, isTopMostScreen: false)
        {
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "ShipyardOfflineSetup";

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);

            Center(Brand.Faction, -0.28f, Brand.Accent, 0.95f);
            Center("OFFLINE SHIPYARD  //  LOCAL GIT", -0.245f, Brand.AccentDim, 0.8f);
            Center("A version-tracked shipyard on this PC. No account, no network.", -0.21f, Brand.Muted, 0.72f);

            string defPath = string.IsNullOrEmpty(Auth.LocalRepoPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shipyard", "local")
                : Auth.LocalRepoPath;

            AddRowLabel("Folder:", -0.15f);
            _path = AddRowBox(-0.15f, defPath);
            Center("Where the local repo lives (created if it doesn't exist).", -0.115f, Brand.Muted, 0.66f);

            AddRowLabel("Top folder:", -0.07f);
            _root = AddRowBox(-0.07f, Auth.RootFolder);
            Center("Groups all ships, e.g. 'Fleet' or your faction tag. Matches a GitHub shipyard if you ever sync.", -0.035f, Brand.Muted, 0.66f);

            AddRowLabel("Author:", 0.01f);
            _author = AddRowBox(0.01f, Auth.LocalAuthor);
            Center("Stamped on your local commits (no GitHub login offline).", 0.045f, Brand.Muted, 0.66f);

            MakeBtn("Create / Use this local shipyard", new Vector2(0f, 0.115f), new Vector2(0.5f, 0.05f), OnCreate);

            MakeBtn("Switch to Online (GitHub) instead", new Vector2(0f, 0.18f), new Vector2(0.5f, 0.045f), OnOnline);
            MakeBtn("Close", new Vector2(0f, 0.24f), new Vector2(0.3f, 0.045f), () => CloseScreen(false));

            Center(Brand.Classified, 0.29f, Brand.AccentDim, 0.6f);
        }

        private void OnCreate()
        {
            string path = (_path.Text ?? "").Trim();
            string root = (_root.Text ?? "").Trim();
            string author = (_author.Text ?? "").Trim();
            // Single source of truth for the empty-path check: guard HERE (before CloseScreen, so we can
            // return on invalid input). InitLocalRepo re-checks defensively but won't be reached blank.
            if (path.Length == 0) { ShipyardRunner.ShowMessage("Pick a folder for the local shipyard."); return; }
            // root/author may be blank: Auth.RootFolder falls back to "Fleet" and Auth.LocalAuthor to
            // Environment.UserName. The boxes above are prefilled with those effective defaults, so a blank
            // value here means the user cleared a shown default; accepting it is intentional.
            CloseScreen(false);
            ShipyardApi.InitLocalRepo(path, author, root, () => ShipyardApi.OpenShipyard(null));
        }

        private void OnOnline()
        {
            Auth.SetMode("online");
            CloseScreen(false);
            MyGuiSandbox.AddScreen(new SettingsScreen());
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
