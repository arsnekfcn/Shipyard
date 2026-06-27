using System;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ShipyardPlugin
{
    // First-boot fork: pick the Online (GitHub) or Offline (local git) experience. Shown once, when no
    // mode has been chosen and the user isn't already signed in (Auth.ModeChosen == false). The choice
    // is changeable later from the Account screen.
    public class ModeChooseScreen : MyGuiScreenDebugBase
    {
        public ModeChooseScreen()
            : base(new Vector2(0.5f, 0.5f), new Vector2(0.7f, 0.62f), Brand.Bg, isTopMostScreen: false)
        {
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "ShipyardModeChoose";

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);

            Center(Brand.Faction, -0.26f, Brand.Accent, 0.95f);
            Center(Brand.Product + "    " + Brand.Version, -0.225f, Brand.AccentDim, 0.8f);
            Center("Choose how you want to run the Shipyard. You can change this later in Account.", -0.18f, Brand.Muted, 0.72f);
            Controls.Add(Frame.MakeDivider(-0.15f, 0.62f));

            // ---- Online ----
            MakeBtn("ONLINE   //   GitHub", new Vector2(0f, -0.085f), new Vector2(0.5f, 0.05f), OnOnline);
            Center("Sign in with GitHub. Share a shipyard with a crew. Publish, review, merge.", -0.035f, Brand.Muted, 0.66f);
            Center("Needs an internet connection and a GitHub account.", -0.01f, Brand.Muted, 0.62f);

            // ---- Offline ----
            MakeBtn("OFFLINE   //   Local only", new Vector2(0f, 0.075f), new Vector2(0.5f, 0.05f), OnOffline);
            Center("A local git shipyard on this PC. Browse, install, save versions. No account, no network.", 0.125f, Brand.Muted, 0.66f);
            Center("It's a normal git repo; host it yourself later if you ever want to.", 0.15f, Brand.Muted, 0.62f);

            Center(Brand.Classified, 0.27f, Brand.AccentDim, 0.6f);
        }

        private void OnOnline()
        {
            // Commit to online immediately (unlike offline, which defers): online has no irreversible
            // repo-creation step, and the mode is freely changeable later from the Account screen. So if
            // the user backs out of SettingsScreen without configuring owner/repo/token, that's harmless.
            Auth.SetMode("online");
            CloseScreen(false);
            MyGuiSandbox.AddScreen(new SettingsScreen());
        }

        private void OnOffline()
        {
            // Don't commit to offline until setup actually creates the repo (so backing out re-shows this).
            CloseScreen(false);
            MyGuiSandbox.AddScreen(new OfflineSetupScreen());
        }

        private void Center(string text, float y, Vector4 color, float scale)
            => Controls.Add(Frame.CenterLabel(text, y, color, scale));

        private void MakeBtn(string text, Vector2 pos, Vector2 size, Action onClick)
            => Controls.Add(Frame.MakeButton(text, pos, size, _ => onClick()));
    }
}
