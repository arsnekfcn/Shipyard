using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace ShipyardPlugin
{
    // Draws colored wireframe boxes around changed blocks each frame.
    // Boxes are grid-local positions on a target grid; redrawn from the
    // grid's live world matrix so they follow it. Plugin.Update() calls Draw().
    // When the player looks at a box within short range, its change label floats over the block.
    internal static class HighlightManager
    {
        // Category (added/removed/changed/recolored/data) is authoritative; the DRAW color is resolved
        // from settings configurable palette per frame, so users can recolor categories if the need/want to.
        // OldData/NewData: optional settings/custom-data payload (set on "± data" boxes) shown
        // line-by-line in TextDiffScreen when the player aims at the box and presses Ctrl+Shift+D.
        public struct Box { public Vector3D Center; public Vector3D Size; public string Category; public string Label; public string OldData, NewData; }

        private static IMyEntity _grid;
        private static List<Box> _boxes;
        private static readonly MyStringId LineMat = MyStringId.GetOrCompute("Square");

        // The 12 edges of a box, as index pairs into the 8 corners built in (x,y,z) bit order
        // (corner i: bit2=±x, bit1=±y, bit0=±z). Used by the xray wireframe.
        private static readonly int[,] _edges =
        {
            {0,4},{1,5},{2,6},{3,7},   // x edges
            {0,2},{1,3},{4,6},{5,7},   // y edges
            {0,1},{2,3},{4,5},{6,7},   // z edges
        };

        const double LabelRange = 80.0;   // meters: how far the crosshair ray reaches for a label
        const double AimPad = 0.35;       // meters: inflate each box so edges are easy to aim at
        const int BoxLineWireDivide = 1;        // DrawTransparentBox: wireframe subdivision (1 = box edges only)
        const float BoxLineThickness = 0.05f;   // DrawTransparentBox: wireframe line thickness
        const float BoxLineAlpha = 1f;          // DrawTransparentBox: line color alpha multiplier
        const float LabelTextScale = 0.8f;      // floating change-label text scale

        // Reusable scratch buffers (Draw runs single-threaded on the main thread): avoid per-box/per-frame allocation.
        private static readonly Vector3D[] _corners = new Vector3D[8];
        private static readonly List<KeyValuePair<double, int>> _byDist = new List<KeyValuePair<double, int>>();

        // Per-frame resolved palette/visibility cache, keyed by Categories[] index, so we don't re-parse the
        // hex color string or re-do the HashSet lookup once per box per frame. Rebuilt at the top of Draw().
        // Sized to the 5 canonical categories (see Categories[]); CatCount must match Categories.Length.
        const int CatCount = 5;
        private static readonly bool[] _catShown = new bool[CatCount];
        private static readonly Color[] _catColor = new Color[CatCount];
        private static int CategoryIndex(string category)
        {
            for (int i = 0; i < Categories.Length; i++) if (Categories[i] == category) return i;
            return -1;
        }
        private static void RefreshPalette()
        {
            for (int i = 0; i < Categories.Length; i++)
            {
                _catShown[i] = Auth.IsDiffShown(Categories[i]);
                _catColor[i] = HexToColor(Auth.DiffColorHex(Categories[i]));
            }
        }
        private static bool ShownCached(string category)
        {
            int idx = CategoryIndex(category);
            return idx < 0 ? Auth.IsDiffShown(category) : _catShown[idx];
        }
        private static Color ColorCached(string category)
        {
            int idx = CategoryIndex(category);
            return idx < 0 ? HexToColor(Auth.DiffColorHex(category)) : _catColor[idx];
        }

        // Drawing budget: a big diff can be thousands of boxes. We draw only the nearest HighlightCount
        // (user-configurable, see MaxDraw below), recomputed as the player moves. Distance cap + per-
        // category show/hide also filter the set.
        private static List<int> _visible;   // indices of the currently-drawn subset
        private static int _recomputeTick;

        public static bool Active => _grid != null && _boxes != null && _boxes.Count > 0;

        public static void Set(IMyEntity grid, List<Box> boxes) { _grid = grid; _boxes = boxes; _visible = null; _recomputeTick = 0; _lookedAt = -1; }
        public static void Clear() { _grid = null; _boxes = null; _visible = null; _lookedAt = -1; }

        // The box the crosshair is currently on (set each frame by DrawLookedAtLabel), so a key
        // press can act on "what I'm looking at" e.g. open the data diff for a magenta box.
        private static int _lookedAt = -1;
        public static bool TryGetLookedAtData(out string label, out string oldData, out string newData)
        {
            label = oldData = newData = null;
            var boxes = _boxes;
            int i = _lookedAt;
            if (boxes == null || i < 0 || i >= boxes.Count) return false;
            if (boxes[i].OldData == null && boxes[i].NewData == null) return false;
            label = boxes[i].Label; oldData = boxes[i].OldData ?? ""; newData = boxes[i].NewData ?? "";
            return true;
        }

        // The DRAW color for a box, from the user-configurable palette. Resolved once per frame into the
        // _catColor/_catShown caches by RefreshPalette(); read back via ColorCached()/ShownCached().
        private static Color HexToColor(string hex)
        {
            try
            {
                if (string.IsNullOrEmpty(hex) || hex.Length < 6) return Color.White;
                return new Color((byte)Convert.ToInt32(hex.Substring(0, 2), 16),
                                 (byte)Convert.ToInt32(hex.Substring(2, 2), 16),
                                 (byte)Convert.ToInt32(hex.Substring(4, 2), 16));
            }
            catch { return Color.White; }
        }

        // Configured cap on boxes drawn at once (also used by ShipyardApi's "large diff" result text).
        public static int MaxDraw => Auth.HighlightCount;

        // Canonical change categories + resolution from a user-typed alias (name OR color).
        public static readonly string[] Categories = { "added", "removed", "changed", "recolored", "data" };
        public static string CategoryFromAlias(string a)
        {
            switch ((a ?? "").Trim().ToLowerInvariant())
            {
                case "added": case "add": case "green": case "lime": return "added";
                case "removed": case "remove": case "red": return "removed";
                case "changed": case "change": case "replaced": case "orange": case "yellow": return "changed";
                case "recolored": case "recolor": case "repaint": case "paint": case "cyan": return "recolored";
                case "data": case "inv": case "inventory": case "customdata": case "cargo": case "purple": case "magenta": return "data";
                default: return null;
            }
        }

        public static void Draw()
        {
            // Snapshot the static state into locals so a mid-frame Clear()/Set() (should only ever happen on
            // the main thread, but the guarding is otherwise inconsistent) cannot null/shorten the collections
            // we're iterating below.
            var grid = _grid;
            var boxes = _boxes;
            if (grid == null || boxes == null) return;
            if (grid.MarkedForClose || grid.Closed) { Clear(); return; }
            MatrixD gm = grid.WorldMatrix;

            // Resolve the per-category shown-flag + parsed color once per frame instead of per box.
            RefreshPalette();

            // Select the visible subset (honors show/hide, the distance cap, and the count cap),
            // refreshed ~5Hz; draw it every frame.
            if (_visible == null || (++_recomputeTick % 12) == 0) RecomputeVisible(gm, boxes);
            var visible = _visible;
            foreach (int i in visible) DrawBox(boxes[i], gm);
            DrawLookedAtLabel(gm, visible, boxes);
        }

        private static void DrawBox(Box b, MatrixD gm)
        {
            var col = ColorCached(b.Category);
            if (Auth.XrayHighlights)
            {
                // Xray: draw the 12 box EDGES as depthRead:false lines so the box renders THROUGH blocks
                // but stays a WIREFRAME.
                // Corners are the box extents in grid-local space, transformed by the grid matrix to world.
                // Reuse the shared _corners buffer (Draw is main-thread only) to avoid a per-box allocation.
                var half = b.Size * 0.5;
                int ci = 0;
                for (int xi = -1; xi <= 1; xi += 2)
                    for (int yi = -1; yi <= 1; yi += 2)
                        for (int zi = -1; zi <= 1; zi += 2)
                            _corners[ci++] = Vector3D.Transform(b.Center + new Vector3D(xi * half.X, yi * half.Y, zi * half.Z), gm);
                for (int e = 0; e < _edges.GetLength(0); e++)
                    VRageRender.MyRenderProxy.DebugDrawLine3D(_corners[_edges[e, 0]], _corners[_edges[e, 1]], col, col, /*depthRead*/ false, false);
                return;
            }
            MatrixD wm = gm;
            wm.Translation = Vector3D.Transform(b.Center, gm);
            var local = new BoundingBoxD(-(b.Size * 0.5), b.Size * 0.5);
            MySimpleObjectDraw.DrawTransparentBox(ref wm, ref local, ref col,
                MySimpleObjectRasterizer.Wireframe, BoxLineWireDivide, BoxLineThickness, null, LineMat, false, -1,
                VRageRender.MyBillboard.BlendTypeEnum.Standard, BoxLineAlpha, null);
        }

        // The visible subset: shown categories, within the distance cap, the nearest HighlightCount.
        private static void RecomputeVisible(MatrixD gm, List<Box> boxes)
        {
            int max = Auth.HighlightCount;
            int distM = Auth.HighlightDistance;
            double maxDistSq = distM > 0 ? (double)distM * distM : double.MaxValue;
            if (_visible == null) _visible = new List<int>(); else _visible.Clear();

            var cam = MyAPIGateway.Session?.Camera;
            if (cam == null)
            {
                // Camera unavailable: degenerate fallback - takes the first 'max' shown boxes in raw _boxes
                // order, ignoring the distance cap and with no distance ordering (no camera to measure from).
                for (int i = 0; i < boxes.Count && _visible.Count < max; i++) if (ShownCached(boxes[i].Category)) _visible.Add(i);
                return;
            }
            Vector3D camPos = cam.Position;
            // Reuse the shared scratch list (main-thread only) instead of allocating per recompute.
            var byDist = _byDist;
            byDist.Clear();
            for (int i = 0; i < boxes.Count; i++)
            {
                if (!ShownCached(boxes[i].Category)) continue;
                Vector3D wc = Vector3D.Transform(boxes[i].Center, gm);
                double d2 = Vector3D.DistanceSquared(wc, camPos);
                if (d2 > maxDistSq) continue;
                byDist.Add(new KeyValuePair<double, int>(d2, i));
            }
            byDist.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < max && i < byDist.Count; i++) _visible.Add(byDist[i].Value);
        }

        // Float the change text over whichever changed block the player is aiming at, within range.
        // 'subset' = which box indices to test (null = all); matches whatever Draw rendered this frame.
        private static void DrawLookedAtLabel(MatrixD gm, List<int> subset, List<Box> boxes)
        {
            var cam = MyAPIGateway.Session?.Camera;
            if (cam == null) return;

            // Cast the crosshair ray in grid-local space and test it against each box's AABB, so you can
            // aim anywhere on a (possibly large) block. Picks the nearest hit.
            MatrixD inv = MatrixD.Invert(gm);
            Vector3D lo = Vector3D.Transform(cam.Position, inv);
            Vector3D ld = Vector3D.TransformNormal(cam.WorldMatrix.Forward, inv);
            ld.Normalize();
            var ray = new RayD(lo, ld);

            int best = -1;
            double bestT = double.MaxValue;
            var pad = new Vector3D(AimPad);
            int count = subset != null ? subset.Count : boxes.Count;
            for (int j = 0; j < count; j++)
            {
                int i = subset != null ? subset[j] : j;
                if (string.IsNullOrEmpty(boxes[i].Label) || !ShownCached(boxes[i].Category)) continue;
                var half = boxes[i].Size * 0.5 + pad;
                var bb = new BoundingBoxD(boxes[i].Center - half, boxes[i].Center + half);
                double? t = bb.Intersects(ray);
                if (t.HasValue && t.Value >= 0 && t.Value < bestT && t.Value <= LabelRange) { bestT = t.Value; best = i; }
            }
            _lookedAt = best;
            if (best < 0) return;

            // The floating label is an IN-WORLD aiming hint (incl. the "± data ... Ctrl+Shift+D" prompt).
            // Drop it while a menu/cursor is open so it doesn't hang over the panel.
            // It comes back when you close the menu and aim again.
            try { if (MyAPIGateway.Gui != null && MyAPIGateway.Gui.IsCursorVisible) return; } catch { }

            Vector3D wc = Vector3D.Transform(boxes[best].Center, gm);
            var c = ColorCached(boxes[best].Category);
            VRageRender.MyRenderProxy.DebugDrawText3D(wc, boxes[best].Label, c, LabelTextScale,
                false, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, -1, false);
        }
    }
}
