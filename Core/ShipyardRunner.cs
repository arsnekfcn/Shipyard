using System;
using System.Threading;
using Sandbox;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace ShipyardPlugin
{
    // GUI feedback helpers. All message boxes are marshalled onto the main game thread.
    internal static class ShipyardRunner
    {
        public static void InvokeOnMain(Action a)
        {
            try { MySandboxGame.Static.Invoke(a, "Shipyard"); }
            // Do NOT run a() inline here: the action does main-thread-only GUI work
            // (AddScreen / notifications) and running it on this (background) thread is unsafe.
            // Log and drop instead. Invoke throwing is rare in practice.
            catch (Exception ex) { Plugin.Log("Invoke failed (" + ex.Message + "); dropping action"); }
        }

        // Handle for an async-created busy indicator. A busy state is shown WITHOUT a modal overlay:
        // as the Shipyard menu's own status line if it's open, else as a HUD notification - so an
        // operation never "blasts a loading screen" at the player (sign-in is the one modal exception).
        public sealed class BoxHandle { public MyGuiScreenBase Screen; public IMyHudNotification Note; public bool Menu; }

        // Upper bound on the HUD busy-notification lifetime. CloseBox(h) hides it explicitly once the
        // op finishes; this is just a safety cap so a leaked note self-expires. Hide() still works after
        // natural expiry. Kept large so it does not vanish out from under a slow (>2 min) operation.
        private const int BusyNoteLifetimeMs = 600000;

        public static BoxHandle ShowBusy(string spinnerMsg)
        {
            var h = new BoxHandle();
            string msg = string.IsNullOrEmpty(spinnerMsg) ? "Working..." : spinnerMsg;
            InvokeOnMain(() =>
            {
                try
                {
                    if (ShipyardScreen.IsActiveOpen) { h.Menu = true; ShipyardScreen.SetStatus("> " + msg); }
                    else if (MyAPIGateway.Utilities != null) { h.Note = MyAPIGateway.Utilities.CreateNotification(msg, BusyNoteLifetimeMs, "Blue"); h.Note.Show(); }
                }
                catch (Exception ex) { Plugin.Log("ShowBusy failed: " + ex.Message); }
            });
            return h;
        }

        // Transient HUD info (non-modal). Used for informational results that don't need an OK click.
        public static void Notify(string text, int ms = 6000)
        {
            InvokeOnMain(() =>
            {
                try
                {
                    if (MyAPIGateway.Utilities != null) { MyAPIGateway.Utilities.ShowNotification(text, ms); return; }
                    AddBox(text);   // no HUD (menu) -> fall back to a box
                }
                catch (Exception ex) { Plugin.Log("Notify failed: " + ex.Message); }
            });
        }

        public static BoxHandle ShowBusyModal(string spinnerMsg)
        {
            var h = new BoxHandle();
            InvokeOnMain(() =>
            {
                try
                {
                    h.Screen = new TerminalScreen("ASSET REGISTRY", string.IsNullOrEmpty(spinnerMsg) ? "Working..." : spinnerMsg,
                        "Reading the registry...", null, new Vector2(0.62f, 0.36f));
                    MyGuiSandbox.AddScreen(h.Screen);
                }
                catch (Exception ex) { Plugin.Log("ShowBusyModal failed: " + ex.Message); }
            });
            return h;
        }

        // Animated sign-in terminal: shows the device URL + one-time code while it waits for authorization.
        public static BoxHandle ShowSignIn(string url, string code)
        {
            var h = new BoxHandle();
            InvokeOnMain(() =>
            {
                try
                {
                    h.Screen = new TerminalScreen("OPERATOR AUTHORIZATION", "AWAITING AUTHORIZATION",
                        "Open this in a browser (it may have opened for you):\n" + url +
                        "\n\nthen enter this one-time code:", code,
                        // Tall enough that the big code + "copied" hint + spinner clear the bottom buttons.
                        new Vector2(0.74f, 0.58f));
                    MyGuiSandbox.AddScreen(h.Screen);
                }
                catch (Exception ex) { Plugin.Log("ShowSignIn failed: " + ex.Message); }
            });
            return h;
        }

        // Dismiss whichever busy indicator was shown (modal sign-in screen, HUD note, or menu status).
        public static void CloseBox(BoxHandle h)
        {
            if (h == null) return;
            InvokeOnMain(() =>
            {
                try { h.Screen?.CloseScreen(false); } catch (Exception ex) { Plugin.Log("CloseBox: CloseScreen failed: " + ex.Message); }
                try { h.Note?.Hide(); } catch (Exception ex) { Plugin.Log("CloseBox: Note.Hide failed: " + ex.Message); }
                try { if (h.Menu) ShipyardScreen.SetStatus(""); } catch (Exception ex) { Plugin.Log("CloseBox: SetStatus failed: " + ex.Message); }
            });
        }

        // Create + show a result box as an in-style Mandate dialog. MAIN THREAD ONLY.
        private static void AddBox(string text)
        {
            MyGuiSandbox.AddScreen(new DialogScreen("SHIPYARD", text, false, null));
        }

        // Errors / must-read messages: always a modal box.
        public static void ShowMessage(string text) => InvokeOnMain(() => AddBox(text));

        // Operation RESULTS / tips: a modal box WITH a "Don't show this again" checkbox - until the user
        // opts out (Auth.HidePopups), after which results appear as a white HUD notification instead.
        public static void ShowResult(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (Auth.HidePopups) { Notify(text); return; }
            InvokeOnMain(() => MyGuiSandbox.AddScreen(new DialogScreen("SHIPYARD", text, false, null, () => Auth.SetHidePopups(true))));
        }

        // In-style CONFIRM / CANCEL dialog; fires onResult(true) when the operator confirms.
        public static void Confirm(string title, string body, Action<bool> onResult)
            => InvokeOnMain(() => MyGuiSandbox.AddScreen(new DialogScreen(title, body, true, onResult)));

        // Show a non-modal busy indicator, run work() on a background thread, then close the busy
        // indicator and hand the result to onSuccess (on the main thread). Errors show a result box.
        public static void RunWithBusyThen<T>(string busyText, Func<T> work, Action<T> onSuccess)
            => RunCore(ShowBusy(busyText), work, onSuccess);

        // Same, but behind a MODAL loading screen (with EXIT). For "I clicked, I'm waiting" actions
        // like Details/Specs. If the player EXITs the overlay, the late result is dropped instead of
        // popping up over whatever they moved on to.
        public static void RunWithBusyModalThen<T>(string busyText, Func<T> work, Action<T> onSuccess)
            => RunCore(ShowBusyModal(busyText), work, onSuccess);

        private static void RunCore<T>(BoxHandle busy, Func<T> work, Action<T> onSuccess)
        {
            var t = new Thread(() =>
            {
                T result = default(T);
                string error = null;
                try { result = work(); }
                catch (Exception ex) { ShipyardErrors.WipeIfExpired(ex); error = ShipyardErrors.Explain(ex); Plugin.Log("work failed: " + ex); }

                string capturedError = error;
                T capturedResult = result;
                InvokeOnMain(() =>
                {
                    // A modal busy screen the USER already dismissed (EXIT/Esc) = "never mind":
                    // swallow the result/error instead of resurfacing it later. (Checked before
                    // CloseBox, which would also close the screen.)
                    // NOTE: this only applies to modal busy (Screen != null). Non-modal busy
                    // (ShowBusy: HUD note / menu status, Screen == null) is INTENTIONALLY
                    // non-cancelable - there is no EXIT button, so its result always surfaces.
                    // Do not "fix" the asymmetry by suppressing those too.
                    bool canceled = busy.Screen != null && !busy.Screen.IsOpened;
                    CloseBox(busy);
                    if (canceled) return;
                    if (capturedError != null) { AddBox(capturedError); return; }
                    try { onSuccess(capturedResult); }
                    catch (Exception ex) { Plugin.Log("onSuccess failed: " + ex); AddBox("Error: " + ex.Message); }
                });
            });
            t.IsBackground = true;
            t.Name = "ShipyardOp";
            t.Start();
        }

        // Convenience: work() returns the text to show as the (dismissible) result.
        public static void RunWithBusy(string busyText, Func<string> work)
            => RunWithBusyThen(busyText, work, text => ShowResult(text));
    }
}
