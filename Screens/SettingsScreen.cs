using System;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ShipyardPlugin
{
    // Account hub. Two states:
    //   * NOT signed in  -> a minimal onboarding funnel: sign in (or use your own app), or work offline.
    //   * Signed in      -> the full dashboard: connect to an existing repo, create a brand-new one,
    //                        manage access, initialize / open the active shipyard.
    // The repo (owner/repo/top-folder) form lives on ConnectRepoScreen - we don't ask for it up front,
    // since a fresh user can just have one created for them.
    public class SettingsScreen : MyGuiScreenDebugBase
    {
        private MyGuiControlTextbox _clientId;

        // ---- shared row geometry (left-origin controls) ----
        private const float RowLeftX = -0.21f;       // left edge / X-origin of the input control
        private const float RowBoxWidth = 0.47f;     // full-width text box width
        private const float RowHeight = 0.035f;      // row control height
        private const float RowLabelX = -0.31f;      // left-origin X of the row caption label

        public SettingsScreen()
            : base(new Vector2(0.5f, 0.5f), new Vector2(0.74f, 0.96f),
                   Brand.Bg, isTopMostScreen: false)
        {
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "ShipyardSettingsScreen";

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);

            // ---- Mandate brand header ----
            Center(Brand.Faction, -0.405f, Brand.Accent, 0.95f);
            Center(Brand.Product + "    " + Brand.Version, -0.365f, Brand.AccentDim, 0.8f);
            Center("OPERATOR AUTHENTICATION  //  " + Brand.Slogan, -0.33f, Brand.Muted, 0.72f);

            string logo = Brand.LogoPath();
            if (logo != null)
            {
                try
                {
                    // portrait crest (~689x1004) -> keep the aspect so it isn't squished
                    var img = new MyGuiControlImage(new Vector2(-0.30f, -0.355f), new Vector2(0.072f, 0.105f),
                        null, null, null, null, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER);
                    img.SetTexture(logo, null);
                    Controls.Add(img);
                }
                catch (Exception ex) { Plugin.Log("logo render failed: " + ex.Message); }
            }

            bool signedIn = Auth.HasToken;
            var status = new MyGuiControlLabel(new Vector2(0f, -0.30f), null,
                signedIn ? "CREDENTIALS ACCEPTED  -  operator @" + (string.IsNullOrEmpty(Auth.Login) ? "?" : Auth.Login)
                         : "NO CREDENTIALS ON FILE  -  authenticate to proceed")
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
              ColorMask = signedIn ? Brand.Ok : Brand.Warn };
            Controls.Add(status);

            if (!signedIn)
                BuildSignedOut();
            else
                BuildDashboard();

            Center(Brand.Classified, 0.45f, Brand.AccentDim, 0.62f);
        }

        // ---- onboarding funnel (no token yet) ----------------------------------------------------
        // Just the ways IN: sign in with GitHub (optionally with your own app), or work offline. We
        // don't ask for owner/repo here - once signed in you either connect to an existing repo or
        // have a fresh one created for you.
        void BuildSignedOut()
        {
            // Sign in. Apply whatever's in the Client ID box first, so an advanced user can paste their
            // own app's id and sign in through it in one step (blank -> built-in app).
            MakeBtn("Sign in with GitHub", new Vector2(0f, -0.22f), new Vector2(0.5f, 0.055f),
                () => { Auth.SetClientId((_clientId.Text ?? "").Trim()); ShipyardApi.SignIn(() => RecreateControls(false)); });

            Center("New here?  Sign in and Shipyard can create a private repo for you.",
                -0.16f, Brand.Muted, 0.72f);

            // ---- advanced: bring your own GitHub App ----
            AddRowLabel("Client ID:", -0.08f);
            _clientId = AddRowBox(-0.08f, Auth.CustomClientId);
            Center("(advanced) leave blank for the built-in sign-in app, or use your OWN app below", -0.04f, Brand.Muted, 0.72f);

            MakeBtn("Use your own GitHub App  (least-privilege)  >", new Vector2(0f, 0.005f), new Vector2(0.54f, 0.045f),
                () => { CloseScreen(false); MyGuiSandbox.AddScreen(new ByoAppScreen()); });

            // ---- or skip accounts entirely ----
            MakeBtn("Work offline (local git only)  >", new Vector2(0f, 0.085f), new Vector2(0.5f, 0.045f),
                () => { CloseScreen(false); MyGuiSandbox.AddScreen(new OfflineSetupScreen()); });

            MakeBtn("Close", new Vector2(0f, 0.15f), new Vector2(0.3f, 0.04f), () => CloseScreen(false));
        }

        // ---- full dashboard (signed in) --------------------------------------------------------
        void BuildDashboard()
        {
            // Sign out wipes the saved token + login (repo / top-folder / client-id are kept) and drops
            // you on the signed-out screen, where you re-auth however you want - the built-in app, your
            // own Client ID, or a full BYO GitHub App. We deliberately do NOT auto-launch a browser flow:
            // that forced the default app and got in the way of re-authing with a custom one.
            MakeBtn("Sign out", new Vector2(0f, -0.25f), new Vector2(0.3f, 0.045f),
                () => { Auth.SignOut(); RecreateControls(false); });

            // Show the active shipyard so "the current connection" is always visible.
            bool configured = Auth.IsConfigured;
            Center(configured
                    ? "ACTIVE SHIPYARD:   " + Auth.RepoOwner + "/" + Auth.RepoName + "   (" + Auth.RootFolder + ")"
                    : "No shipyard connected yet - connect to one or create a new one below.",
                -0.185f, configured ? Brand.Ok : Brand.Muted, 0.78f);

            // The two ways to get a target repo.
            MakeBtn("Connect to an existing repo  >", new Vector2(0f, -0.13f), new Vector2(0.54f, 0.05f),
                () => { CloseScreen(false); MyGuiSandbox.AddScreen(new ConnectRepoScreen()); });
            MakeBtn("Create a new Shipyard repo for me  >", new Vector2(0f, -0.07f), new Vector2(0.54f, 0.05f),
                () => { CloseScreen(false); MyGuiSandbox.AddScreen(new CreateRepoScreen()); });

            // Requester path: once an admin adds @you, accept the invite here to gain access.
            MakeBtn("Accept repo invitation", new Vector2(-0.14f, -0.005f), new Vector2(0.26f, 0.045f),
                () => ShipyardApi.AcceptInvitations(null));
            MakeBtn("Manage access (admin)", new Vector2(0.14f, -0.005f), new Vector2(0.26f, 0.045f),
                () => { if (!configured) { ShipyardRunner.ShowMessage("Connect a repo first."); return; } CloseScreen(false); ShipyardApi.OpenManageAccess(); });

            if (configured)
            {
                MakeBtn("Initialize repo", new Vector2(-0.14f, 0.055f), new Vector2(0.26f, 0.045f), OnInitRepo);
                MakeBtn("Open Shipyard", new Vector2(0.14f, 0.055f), new Vector2(0.26f, 0.045f),
                    () => { CloseScreen(false); ShipyardApi.OpenShipyard(null); });
            }

            // Switch to the local-only experience (no account/network). Mode flips to offline only once
            // a local repo is actually created in the setup screen, so backing out leaves you online.
            MakeBtn("Work offline (local git only)  >", new Vector2(0f, 0.12f), new Vector2(0.5f, 0.045f),
                () => { CloseScreen(false); MyGuiSandbox.AddScreen(new OfflineSetupScreen()); });

            MakeBtn("Close", new Vector2(0f, 0.185f), new Vector2(0.3f, 0.04f), () => CloseScreen(false));
        }

        // Centered flavor label helper.
        void Center(string text, float y, Vector4 color, float scale)
            => Controls.Add(Frame.CenterLabel(text, y, color, scale));

        void OnInitRepo()
        {
            ShipyardRunner.Confirm("INITIALIZE REPO",
                "Seed " + Auth.RepoOwner + "/" + Auth.RepoName + " with shipyard structure\n" +
                "(.gitattributes, .gitignore, CODEOWNERS, " + Auth.RootFolder + "/)?\n" +
                "Existing files are left untouched.",
                ok => { if (ok) ShipyardApi.InitRepo(); });
        }

        void AddRowLabel(string text, float y)
        {
            var lbl = new MyGuiControlLabel(new Vector2(RowLabelX, y), null, text)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER };
            Controls.Add(lbl);
        }

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
