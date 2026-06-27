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
    // A proper line-by-line OLD vs NEW text diff (LCS), used for custom data
    // changes: '-' lines in red (removed), '+' lines in green (added), context dimmed. Opened by
    // aiming at a "± data" highlight box and pressing Ctrl+Shift+D.
    public class TextDiffScreen : MyGuiScreenDebugBase
    {
        private static readonly Vector4 RemColor = new Vector4(1f, 0.45f, 0.45f, 1f);
        private static readonly Vector4 InsColor = new Vector4(0.55f, 1f, 0.55f, 1f);
        private static readonly Vector4 CtxColor = new Vector4(0.65f, 0.68f, 0.72f, 1f);

        private readonly string _title;
        private readonly List<KeyValuePair<string, Vector4>> _lines;

        public TextDiffScreen(string title, string oldText, string newText)
            : base(new Vector2(0.5f, 0.5f), new Vector2(0.86f, 0.7f), Brand.Bg, isTopMostScreen: true)
        {
            _title = title ?? "data";
            _lines = BuildDiff(oldText ?? "", newText ?? "");
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "ShipyardTextDiff";

        public override void HandleInput(bool receivedFocusInThisUpdate)
        {
            base.HandleInput(receivedFocusInThisUpdate);
            if (MyInput.Static.IsNewKeyPressed(MyKeys.Escape)) CloseScreen(false);
        }

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            AddCaption("DATA DIFF  -  " + _title, Brand.Accent, null, 0.9f);

            var list = new MyGuiControlList(new Vector2(0f, 0.005f), new Vector2(0.82f, 0.52f),
                null, null, MyGuiControlListStyleEnum.Default);
            var items = new List<MyGuiControlBase>();
            foreach (var line in _lines)
            {
                var lbl = new MyGuiControlLabel(new Vector2(-0.40f, 0f), null,
                    string.IsNullOrWhiteSpace(line.Key) ? " " : line.Key, line.Value, 0.74f)
                { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER };
                items.Add(lbl);
            }
            list.InitControls(items);
            Controls.Add(list);

            var close = AddButton("Close", (Action<MyGuiControlButton>)(b => CloseScreen(false)), null, null);
            close.Position = new Vector2(0f, 0.285f);   // clear of the footer strip
            Controls.Add(Frame.MakeFooter(0.345f));
        }

        // Classic LCS line diff. Inputs are block custom data.
        private static List<KeyValuePair<string, Vector4>> BuildDiff(string oldText, string newText)
        {
            var result = new List<KeyValuePair<string, Vector4>>();

            if (oldText == newText)
            {
                result.Add(new KeyValuePair<string, Vector4>("(no text difference - the change is in non-text mod settings)", CtxColor));
                return result;
            }

            var a = oldText.Replace("\r", "").Split('\n');
            var b = newText.Replace("\r", "").Split('\n');

            // Cap bounds the synchronous O(n*m) LCS DP table built below when the screen opens.
            const int Cap = 600;
            const int PreviewLines = 40;
            if (a.Length > Cap || b.Length > Cap)
            {
                result.Add(new KeyValuePair<string, Vector4>("(too large for a line diff - showing both versions truncated)", CtxColor));
                result.Add(new KeyValuePair<string, Vector4>("---- OLD ----", RemColor));
                for (int i = 0; i < Math.Min(a.Length, PreviewLines); i++) result.Add(new KeyValuePair<string, Vector4>("- " + a[i], RemColor));
                result.Add(new KeyValuePair<string, Vector4>("---- NEW ----", InsColor));
                for (int i = 0; i < Math.Min(b.Length, PreviewLines); i++) result.Add(new KeyValuePair<string, Vector4>("+ " + b[i], InsColor));
                return result;
            }

            // LCS table (quadratic, hence the Cap above)
            var dp = new int[a.Length + 1, b.Length + 1];
            for (int i = a.Length - 1; i >= 0; i--)
                for (int j = b.Length - 1; j >= 0; j--)
                    dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

            int x = 0, y = 0;
            while (x < a.Length && y < b.Length)
            {
                if (a[x] == b[y]) { result.Add(new KeyValuePair<string, Vector4>("  " + a[x], CtxColor)); x++; y++; }
                else if (dp[x + 1, y] >= dp[x, y + 1]) { result.Add(new KeyValuePair<string, Vector4>("- " + a[x], RemColor)); x++; }
                else { result.Add(new KeyValuePair<string, Vector4>("+ " + b[y], InsColor)); y++; }
            }
            while (x < a.Length) { result.Add(new KeyValuePair<string, Vector4>("- " + a[x], RemColor)); x++; }
            while (y < b.Length) { result.Add(new KeyValuePair<string, Vector4>("+ " + b[y], InsColor)); y++; }
            return Collapse(result);
        }

        // Collapse long runs of unchanged context so the changed lines aren't buried
        private static List<KeyValuePair<string, Vector4>> Collapse(List<KeyValuePair<string, Vector4>> all)
        {
            const int ctx = 3;
            var changed = new bool[all.Count];
            for (int i = 0; i < all.Count; i++)
                if (all[i].Key.StartsWith("- ") || all[i].Key.StartsWith("+ ")) changed[i] = true;

            var keep = new bool[all.Count];
            for (int i = 0; i < all.Count; i++)
                if (changed[i])
                    for (int j = Math.Max(0, i - ctx); j <= Math.Min(all.Count - 1, i + ctx); j++) keep[j] = true;

            var outp = new List<KeyValuePair<string, Vector4>>(all.Count);
            int k = 0;
            while (k < all.Count)
            {
                if (keep[k]) { outp.Add(all[k]); k++; continue; }
                int start = k;
                while (k < all.Count && !keep[k]) k++;
                outp.Add(new KeyValuePair<string, Vector4>("        . . .  " + (k - start) + " unchanged line" + (k - start == 1 ? "" : "s") + "  . . .", CtxColor));
            }
            return outp;
        }
    }
}
