using System;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ShipyardPlugin
{
    // "Bring your own GitHub App." the least-trust / least-privilege pre-made online tier.
    public class ByoAppScreen : MyGuiScreenDebugBase
    {
        private MyGuiControlTextbox _clientId;

        public ByoAppScreen()
            : base(new Vector2(0.5f, 0.5f), new Vector2(0.84f, 0.94f), Brand.Bg, isTopMostScreen: false)
        {
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "ShipyardByoApp";

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            const float L = -0.40f;     // left margin for instruction lines
            const float Step = 0.74f;   // text scale for STEP headers
            const float Body = 0.68f;   // text scale for body instruction lines

            Center(Brand.Faction, -0.44f, Brand.Accent, 0.9f);
            Center("USE YOUR OWN GITHUB APP", -0.41f, Brand.AccentDim, 0.78f);
            Center("Least-privilege, scoped to ONE repo. You create + control it; the author never touches it.", -0.385f, Brand.Muted, 0.66f);
            Controls.Add(Frame.MakeDivider(-0.36f, 0.76f));

            Left("STEP 1   Open GitHub's create-app page:", L, -0.335f, Brand.Accent, Step);
            MakeBtn("Open GitHub: create an app", new Vector2(0f, -0.295f), new Vector2(0.42f, 0.04f), () => ShipyardApi.OpenCreateAppPage());

            Left("STEP 2   Fill it in, then click 'Create GitHub App':", L, -0.245f, Brand.Accent, Step);
            Left("-  GitHub App name:  anything unique (e.g. Shipyard-yourname)", L + 0.02f, -0.213f, Vector4.One, Body);
            Left("-  Homepage URL:  anything (e.g. your repo's URL)", L + 0.02f, -0.183f, Vector4.One, Body);
            Left("-  Webhook:  UNCHECK 'Active'", L + 0.02f, -0.153f, Vector4.One, Body);
            Left("-  Repository permissions -> Contents:  Read and write", L + 0.02f, -0.123f, Vector4.One, Body);
            Left("-  Repository permissions -> Pull requests:  Read and write", L + 0.02f, -0.093f, Vector4.One, Body);
            Left("-  CHECK 'Enable Device Flow'", L + 0.02f, -0.063f, Vector4.One, Body);
            Left("-  (optional) Administration: Read and write  -  ONLY if you want to manage crew", L + 0.02f, -0.033f, Brand.Muted, 0.66f);
            Left("    access in-game; only you can use it.   'Where installed?':  Any account", L + 0.02f, -0.006f, Brand.Muted, 0.66f);

            Left("STEP 3   On the new app's page:", L, 0.04f, Brand.Accent, Step);
            Left("-  UNCHECK 'Expire user authorization tokens'  ->  Save changes", L + 0.02f, 0.072f, Vector4.One, Body);
            Left("-  'Install App'  ->  install it on your shipyard repo", L + 0.02f, 0.102f, Vector4.One, Body);

            Left("STEP 4   Paste the Client ID (the app's 'General' page):", L, 0.14f, Brand.Accent, Step);
            Left("Client ID:", L, 0.178f, Vector4.One, Step);
            // textbox sits right after the label
            _clientId = new MyGuiControlTextbox { Position = new Vector2(-0.09f, 0.178f), Size = new Vector2(0.44f, 0.035f) };
            if (!string.IsNullOrEmpty(Auth.CustomClientId)) _clientId.Text = Auth.CustomClientId;
            Controls.Add(_clientId);

            MakeBtn("Save  &  Sign in", new Vector2(-0.27f, 0.235f), new Vector2(0.24f, 0.045f), OnSaveSignIn);
            MakeBtn("<  Back to Account / Repo", new Vector2(0f, 0.235f), new Vector2(0.26f, 0.045f),
                () => { CloseScreen(false); MyGuiSandbox.AddScreen(new SettingsScreen()); });
            MakeBtn("Close", new Vector2(0.27f, 0.235f), new Vector2(0.24f, 0.045f), () => CloseScreen(false));

            Center("The Client ID is PUBLIC. It is safe to share with your crew so they can sign into the same app. You must still provision their access.", 0.29f, Brand.Muted, 0.66f);
            Center(Brand.Classified, 0.44f, Brand.AccentDim, 0.58f);
        }

        private void OnSaveSignIn()
        {
            string cid = (_clientId.Text ?? "").Trim();
            if (cid.Length == 0) { ShipyardRunner.ShowMessage("Paste your app's Client ID first (Step 4)."); return; }
            Auth.SetClientId(cid);
            // Device flow uses Auth.ClientId, which now returns this app's id.
            // Guard: the device flow completes on a background thread and marshals
            // this callback back via InvokeOnMain. If the user closed/navigated away
            // first, the screen is disposed - don't mutate Controls on a dead screen.
            ShipyardApi.SignIn(() => { if (State != MyGuiScreenState.CLOSED) RecreateControls(false); });
        }

        private void Center(string text, float yy, Vector4 color, float scale)
            => Controls.Add(Frame.CenterLabel(text, yy, color, scale));

        private void Left(string text, float x, float yy, Vector4 color, float scale)
            => Controls.Add(new MyGuiControlLabel(new Vector2(x, yy), null, text, color, scale)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER });

        private void MakeBtn(string text, Vector2 pos, Vector2 size, Action onClick)
        {
            var b = Frame.MakeButton(text, pos, size, _ => onClick());
            Controls.Add(b);
        }
    }
}
