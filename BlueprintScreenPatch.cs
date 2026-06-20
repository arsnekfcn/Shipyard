using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRageMath;

namespace ShipyardPlugin
{
    // Inject a "Publish to Shipyard" button into the reworked blueprint screen (F10) by
    // postfixing its RecreateControls. AddButton is a public helper on MyGuiScreenDebugBase
    [HarmonyPatch(typeof(MyGuiBlueprintScreen_Reworked), "RecreateControls")]
    internal static class BlueprintScreenPatch
    {
        // Vertical gap between stacked action buttons, in panel-relative GUI coords.
        private const float ButtonVerticalGap = 0.012f;

        private static void Postfix(MyGuiBlueprintScreen_Reworked __instance)
        {
            try
            {
                // AddButton creates + wires the button, but positions it at the debug cursor
                // Capture it and relocate explicitly so it shows.
                MyGuiControlButton btn = __instance.AddButton("Shipyard",
                    (Action<MyGuiControlButton>)(b => OnOpen(__instance)),
                    (List<MyGuiControlBase>)null,
                    (Vector4?)null);
                if (btn == null) { Plugin.Log("AddButton returned null"); return; }

                // Anchor below the Rename button (bottom action area).
                var anchor = __instance.m_button_Rename;
                if (anchor != null)
                {
                    Vector2 aPos = anchor.Position, aSize = anchor.Size;
                    // Match the game's action buttons exactly: style first (carries a default size),
                    // then origin, then our explicit size + position.
                    btn.VisualStyle = anchor.VisualStyle;
                    btn.OriginAlign = anchor.OriginAlign;
                    btn.Size = aSize;
                    btn.Position = new Vector2(aPos.X, aPos.Y + aSize.Y + ButtonVerticalGap); // just below Rename
                    btn.Visible = true;
                    Plugin.Log($"button placed at ({btn.Position.X:0.000},{btn.Position.Y:0.000}) " +
                               $"anchor=Rename({aPos.X:0.000},{aPos.Y:0.000}) size=({aSize.X:0.000},{aSize.Y:0.000})");
                }
                else
                {
                    Plugin.Log($"anchor not found; button at default ({btn.Position.X:0.000},{btn.Position.Y:0.000})");
                }
            }
            catch (Exception ex) { Plugin.Log("AddButton/placement failed: " + ex); }
            // The repo marker is baked into the blueprint's DisplayName on install, because the
            // content button self-refreshes and would wipe any live tile tinting.
        }

        // Opens the shipyard: an authenticated connectivity/list test (shows a single
        // busy box that auto-closes into the result).
        private static void OnOpen(MyGuiBlueprintScreen_Reworked screen)
        {
            Plugin.Log("Open Shipyard clicked");
            // Pass the currently-selected LOCAL blueprint (if any) so the browser can offer Publish.
            string localName = null;
            try
            {
                var sel = screen.m_selectedBlueprint;
                // Only offer publish for a non-folder local blueprint.
                if (sel != null && !sel.IsDirectory && !string.IsNullOrWhiteSpace(sel.BlueprintName) &&
                    sel.Type == MyBlueprintTypeEnum.LOCAL)
                    localName = sel.BlueprintName;
            }
            catch (Exception ex) { Plugin.Log("read selection failed: " + ex); }
            ShipyardApi.OpenShipyard(localName);
        }
    }
}
