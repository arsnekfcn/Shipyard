using System;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ShipyardPlugin
{
    // Point Shipyard at an EXISTING GitHub repo: enter owner/repo/top-folder (or pick a previously-used
    // shipyard) and re-check access. Reached from the Account screen when signed in. The owner+repo must
    // already exist on GitHub - to make a brand-new one, use "Create a new Shipyard repo" instead.
    public class ConnectRepoScreen : MyGuiScreenDebugBase
    {
        private MyGuiControlTextbox _owner, _name, _root;
        private MyGuiControlCombobox _savedBox;

        // Shared row geometry (left-origin controls) - mirrors the alignment used elsewhere so the
        // owner/repo/top-folder boxes and the saved dropdown all line up to one left edge.
        private const float RowLeftX = -0.21f;
        private const float RowBoxWidth = 0.47f;
        private const float RowHeight = 0.035f;
        private const float RowLabelX = -0.31f;
        private const float RowRightX = RowLeftX + RowBoxWidth;     // shared right edge (0.26)
        private const float SavedBoxWidth = 0.30f;                  // narrower to leave room for Forget
        private const float ForgetWidth = 0.135f;

        public ConnectRepoScreen()
            : base(new Vector2(0.5f, 0.5f), new Vector2(0.74f, 0.72f), Brand.Bg, isTopMostScreen: false)
        {
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "ShipyardConnectRepo";

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);

            Center(Brand.Faction, -0.305f, Brand.Accent, 0.95f);
            Center("CONNECT TO AN EXISTING SHIPYARD", -0.27f, Brand.AccentDim, 0.8f);
            Center("Point Shipyard at a repo that already exists on GitHub.", -0.235f, Brand.Muted, 0.72f);

            // ---- repository ----
            AddRowLabel("Owner:", -0.165f);
            _owner = AddRowBox(-0.165f, Auth.RepoOwner);
            AddRowLabel("Repo name:", -0.12f);
            _name = AddRowBox(-0.12f, Auth.RepoName);
            AddRowLabel("Top folder:", -0.075f);
            _root = AddRowBox(-0.075f, Auth.RootFolder);

            Center("Owner + Repo must exist on GitHub.  Top folder groups all ships.", -0.04f, Brand.Muted, 0.72f);

            // ---- saved shipyards: every repo you've used is remembered for one-click switching ----
            var saved = Auth.SavedRepos;
            if (saved.Count > 0)
            {
                AddRowLabel("Saved:", 0.005f);
                _savedBox = new MyGuiControlCombobox(new Vector2(RowLeftX, 0.005f), new Vector2(SavedBoxWidth, RowHeight))
                { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER };
                long sel = -1;
                for (int i = 0; i < saved.Count; i++)
                {
                    string root = string.IsNullOrEmpty(saved[i].Root) ? "Fleet" : saved[i].Root;
                    _savedBox.AddItem(i, saved[i].Key + "   (" + root + ")", null, null, false);
                    if (string.Equals(saved[i].Owner, Auth.RepoOwner, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(saved[i].Name, Auth.RepoName, StringComparison.OrdinalIgnoreCase)) sel = i;
                }
                if (sel >= 0) _savedBox.SelectItemByKey(sel, false);
                // Wired AFTER the programmatic preselect so building the screen doesn't "switch".
                _savedBox.ItemSelected += OnSavedSelected;
                Controls.Add(_savedBox);
                // Center-origin button: place its center so the right edge lands on RowRightX.
                MakeBtn("Forget", new Vector2(RowRightX - ForgetWidth / 2f, 0.005f), new Vector2(ForgetWidth, RowHeight), OnForgetSaved);
            }

            MakeBtn("Save  &  check access", new Vector2(0f, 0.085f), new Vector2(0.5f, 0.05f), OnSave);
            MakeBtn("<  Back to Account", new Vector2(0f, 0.15f), new Vector2(0.5f, 0.045f),
                () => { CloseScreen(false); MyGuiSandbox.AddScreen(new SettingsScreen()); });

            Center(Brand.Classified, 0.3f, Brand.AccentDim, 0.6f);
        }

        void OnSave()
        {
            // Set the repo FIRST (SaveRepo -> SetRepo), THEN the root in the callback - so SetRootFolder's
            // RememberCurrent() attributes the new top folder to the NEW repo, not the previously-active one.
            string root = (_root.Text ?? "").Trim();
            ShipyardApi.SaveRepo(_owner.Text, _name.Text, () => { Auth.SetRootFolder(root); RecreateControls(false); });
        }

        // Switch to a previously-used shipyard: applies its owner/repo/root and re-checks access.
        // Deferred: ItemSelected fires mid-input-handling; rebuilding controls there throws.
        void OnSavedSelected()
        {
            int i = (int)_savedBox.GetSelectedKey();
            var saved = Auth.SavedRepos;
            if (i < 0 || i >= saved.Count) return;
            var r = saved[i];
            if (string.Equals(r.Owner, Auth.RepoOwner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Name, Auth.RepoName, StringComparison.OrdinalIgnoreCase)) return;   // already active
            ShipyardRunner.InvokeOnMain(() =>
            {
                Auth.SetRootFolder(r.Root);
                ShipyardApi.SaveRepo(r.Owner, r.Name, () => RecreateControls(false));
            });
        }

        void OnForgetSaved()
        {
            int i = (int)_savedBox.GetSelectedKey();
            var saved = Auth.SavedRepos;
            if (i < 0 || i >= saved.Count) { ShipyardRunner.ShowMessage("Pick a saved shipyard to forget."); return; }
            var r = saved[i];
            ShipyardRunner.Confirm("FORGET SHIPYARD",
                "Remove " + r.Key + " from the saved list?\n(Only the shortcut is removed - the repo itself is untouched.)",
                ok => { if (ok) { Auth.ForgetRepo(r.Owner, r.Name); RecreateControls(false); } });
        }

        void Center(string text, float y, Vector4 color, float scale)
            => Controls.Add(Frame.CenterLabel(text, y, color, scale));

        void AddRowLabel(string text, float y)
            => Controls.Add(new MyGuiControlLabel(new Vector2(RowLabelX, y), null, text)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER });

        // Left-origin so the box's LEFT EDGE sits at a fixed X (RowLeftX) regardless of width.
        MyGuiControlTextbox AddRowBox(float y, string text)
        {
            var box = new MyGuiControlTextbox
            {
                Position = new Vector2(RowLeftX, y),
                Size = new Vector2(RowBoxWidth, RowHeight),
                OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER
            };
            if (!string.IsNullOrEmpty(text)) box.Text = text;
            Controls.Add(box);
            return box;
        }

        MyGuiControlButton MakeBtn(string text, Vector2 pos, Vector2 size, Action onClick)
        {
            var b = Frame.MakeButton(text, pos, size, _ => onClick());
            Controls.Add(b);
            return b;
        }
    }
}
