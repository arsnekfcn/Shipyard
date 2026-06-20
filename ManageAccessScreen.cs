using System;
using System.Collections.Generic;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ShipyardPlugin
{
    // Admin: view collaborators + pending invites, add/update a user (read-only / write / admin),
    // and remove. Maps to GitHub collaborator permissions (pull / push / admin).
    public class ManageAccessScreen : MyGuiScreenDebugBase
    {
        // Labels match RoleOf's vocabulary ('read | write | admin') so the picker and the existing-collaborator list agree.
        private static readonly string[] PermLabels = { "read", "write", "admin" };
        private static readonly string[] PermValues = { "pull", "push", "admin" };

        // Shared size for the three footer exit buttons so they stay aligned across the note/view/admin branches.
        private static readonly Vector2 ExitBtnSize = new Vector2(0.21f, 0.045f);

        private readonly ManageData _data;
        private MyGuiControlRadioButtonGroup _group;
        private MyGuiControlTextbox _user;
        private MyGuiControlCombobox _perm;

        public ManageAccessScreen(ManageData data)
            : base(new Vector2(0.5f, 0.5f), new Vector2(0.7f, 0.85f),
                   Brand.Bg, isTopMostScreen: false)
        {
            _data = data ?? new ManageData();
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "ShipyardManageAccessScreen";

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            AddCaption("Manage Access: " + Auth.RepoOwner + "/" + Auth.RepoName, null, null, 0.9f);

            // Token can't manage access (e.g. a least-privilege BYO App without Administration) -> point
            // the user at the web instead of failing on every collaborator call.
            if (!string.IsNullOrEmpty(_data.Note))
            {
                float ny = -0.18f;
                foreach (var line in _data.Note.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
                {
                    Controls.Add(new MyGuiControlLabel(new Vector2(0f, ny), null, line, Brand.Accent, 0.78f)
                    { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER });
                    ny += 0.035f;
                }
                Controls.Add(Frame.MakeFooter(0.40f));
                string url = (_data.RepoUrl ?? ("https://github.com/" + Auth.RepoOwner + "/" + Auth.RepoName)) + "/settings/access";
                const float noteFooterY = 0.10f;   // no list/admin rows in this branch, so the footer sits higher
                MakeBtn("Manage on github.com", new Vector2(-0.225f, noteFooterY), ExitBtnSize, () => ShipyardApi.OpenUrl(url));
                MakeBtn("Open Shipyard", new Vector2(0f, noteFooterY), ExitBtnSize, OnOpenShipyard);
                MakeBtn("Close", new Vector2(0.225f, noteFooterY), ExitBtnSize, () => CloseScreen(false));
                return;
            }

            var status = new MyGuiControlLabel(new Vector2(0f, -0.34f), null,
                _data.IsAdmin ? "You are an admin. You can add, change and remove access."
                              : "View only. You are not an admin of this repo.")
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
              ColorMask = _data.IsAdmin ? Brand.Ok : Brand.Accent };
            Controls.Add(status);

            // collaborators (selectable) + pending invites (info rows)
            _group = new MyGuiControlRadioButtonGroup();
            var tiles = new List<MyGuiControlBase>();
            int key = 0;
            foreach (var c in _data.Collaborators)
            {
                var tile = new MyGuiControlContentButton("@" + c.Login + "    -    " + c.Role, "", null) { Key = key++, UserData = c };
                _group.Add(tile); tiles.Add(tile);
            }
            foreach (var inv in _data.PendingInvites)
                tiles.Add(new MyGuiControlContentButton(inv, "", null) { Key = key++ });   // not in group (info only)

            var list = new MyGuiControlList(new Vector2(0f, -0.16f), new Vector2(0.62f, 0.34f),
                null, null, MyGuiControlListStyleEnum.Default);
            list.InitControls(tiles);
            Controls.Add(list);

            if (tiles.Count == 0)
            {
                var none = new MyGuiControlLabel(new Vector2(0f, -0.16f), null, "(no collaborators yet)")
                { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER };
                Controls.Add(none);
            }

            Controls.Add(Frame.MakeFooter(0.40f));

            if (!_data.IsAdmin)
            {
                const float viewFooterY = 0.34f;   // no add/remove row in view-only mode, so the footer sits lower
                MakeBtn("Refresh", new Vector2(-0.225f, viewFooterY), ExitBtnSize, Reload);
                MakeBtn("Open Shipyard", new Vector2(0f, viewFooterY), ExitBtnSize, OnOpenShipyard);
                MakeBtn("Close", new Vector2(0.225f, viewFooterY), ExitBtnSize, () => CloseScreen(false));
                return;
            }

            // ---- admin controls ----
            var addLbl = new MyGuiControlLabel(new Vector2(-0.31f, 0.10f), null, "Add / set:")
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER };
            Controls.Add(addLbl);

            _user = new MyGuiControlTextbox { Position = new Vector2(-0.04f, 0.10f), Size = new Vector2(0.30f, 0.035f) };
            Controls.Add(_user);
            var userHint = new MyGuiControlLabel(new Vector2(-0.04f, 0.137f), null, "GitHub username")
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, ColorMask = Brand.Muted };
            Controls.Add(userHint);

            // dropdown pulled in from the panel border so its arrow isn't clipped
            _perm = new MyGuiControlCombobox(new Vector2(0.225f, 0.10f), new Vector2(0.19f, 0.035f));
            for (int i = 0; i < PermLabels.Length; i++) _perm.AddItem(i, PermLabels[i], null, null, false);
            _perm.SelectItemByKey(1, false);   // default: write
            Controls.Add(_perm);
            var permHint = new MyGuiControlLabel(new Vector2(0.225f, 0.137f), null, "admin = can manage access")
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, ColorMask = Brand.Muted };
            Controls.Add(permHint);

            MakeBtn("Add / set access", new Vector2(-0.13f, 0.21f), new Vector2(0.26f, 0.045f), OnAdd);
            MakeBtn("Remove selected", new Vector2(0.16f, 0.21f), new Vector2(0.26f, 0.045f), OnRemove);

            const float adminFooterY = 0.30f;   // add/remove row above pushes the footer up vs the view-only branch
            MakeBtn("Refresh", new Vector2(-0.225f, adminFooterY), ExitBtnSize, Reload);
            MakeBtn("Open Shipyard", new Vector2(0f, adminFooterY), ExitBtnSize, OnOpenShipyard);
            MakeBtn("Close", new Vector2(0.225f, adminFooterY), ExitBtnSize, () => CloseScreen(false));
        }

        // Direct route back to the main menu. This screen is reached from Settings, but the place
        // you actually want after an access change is the shipyard itself (Settings stays one click
        // away via its "Account / Repo" button).
        void OnOpenShipyard() { CloseScreen(false); ShipyardApi.OpenShipyard(null); }

        // Close-and-reopen to pull a fresh collaborator/invite list (used by Refresh and after add/remove).
        void Reload() { CloseScreen(false); ShipyardApi.OpenManageAccess(); }

        void OnAdd()
        {
            string u = (_user.Text ?? "").Trim().TrimStart('@');   // mirror AddCollaborator's normalization
            if (u.Length == 0) { ShipyardRunner.ShowMessage("Enter a GitHub username."); return; }
            int idx = (int)_perm.GetSelectedKey();
            if (idx < 0 || idx >= PermValues.Length) idx = 1;
            ShipyardApi.AddCollaborator(u, PermValues[idx], Reload);
        }

        void OnRemove()
        {
            var sel = _group != null && _group.SelectedButton != null ? _group.SelectedButton.UserData as CollabEntry : null;
            if (sel == null) { ShipyardRunner.ShowMessage("Select a collaborator to remove."); return; }
            ShipyardRunner.Confirm("REMOVE ACCESS",
                "Remove @" + sel.Login + " from " + Auth.RepoOwner + "/" + Auth.RepoName + "?",
                ok => { if (ok) ShipyardApi.RemoveCollaborator(sel.Login, Reload); });
        }

        MyGuiControlButton MakeBtn(string text, Vector2 pos, Vector2 size, Action onClick)
        {
            var b = Frame.MakeButton(text, pos, size, _ => onClick());
            Controls.Add(b);
            return b;
        }
    }
}
