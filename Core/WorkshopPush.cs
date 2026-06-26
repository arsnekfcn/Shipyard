using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Sandbox.Engine.Networking;
using VRage.Game;
using VRage.GameServices;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;

namespace ShipyardPlugin
{
    internal static class WorkshopPush
    {
        // Steam rejects Workshop thumbnails >= 1 MB with an obscure box.
        private const long MaxThumbBytes = 1_000_000;

        private static string MapPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shipyard", "workshop.json");

        // "name" -> published item id. Hand-rolled JSON, same style as Auth's config.
        private static Dictionary<string, ulong> LoadMap()
        {
            var map = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(MapPath)) return map;
                foreach (Match m in Regex.Matches(File.ReadAllText(MapPath), "\"([^\"]+)\"\\s*:\\s*\"(\\d+)\""))
                {
                    ulong id;
                    if (ulong.TryParse(m.Groups[2].Value, out id) && id != 0) map[m.Groups[1].Value] = id;
                }
            }
            catch (Exception ex) { Plugin.Log("workshop map load failed: " + ex.Message); }
            return map;
        }

        private static void SaveMap(Dictionary<string, ulong> map)
        {
            try
            {
                var sb = new StringBuilder("{\n");
                bool first = true;
                foreach (var kv in map)
                {
                    if (!first) sb.Append(",\n");
                    first = false;
                    sb.Append("  \"").Append(kv.Key.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\": \"").Append(kv.Value).Append("\"");
                }
                sb.Append("\n}\n");
                Directory.CreateDirectory(Path.GetDirectoryName(MapPath));
                File.WriteAllText(MapPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Plugin.Log("workshop map save failed: " + ex.Message); }
        }

        // Push the local blueprint folder <localName> to the Workshop (create or update-in-place).
        // MAIN THREAD ONLY (it drives the game's GUI publish flow). mapKey identifies the published
        // item in workshop.json (the repo ship path, e.g. "PvE/Anaconda").
        public static void Push(string mapKey, string localDir)
        {
            try
            {
                string bpPath = Path.Combine(localDir, "bp.sbc");
                if (!File.Exists(bpPath)) { ShipyardRunner.ShowMessage("Local blueprint not found:\n" + localDir); return; }

                MyObjectBuilder_Definitions defs; ulong sz;
                if (!MyObjectBuilderSerializer.DeserializeXML(File.ReadAllBytes(bpPath), out defs, out sz) ||
                    defs.ShipBlueprints == null || defs.ShipBlueprints.Length == 0 ||
                    defs.ShipBlueprints[0].CubeGrids == null || defs.ShipBlueprints[0].CubeGrids.Length == 0)
                { ShipyardRunner.ShowMessage("Couldn't read the blueprint:\n" + bpPath); return; }
                var bp = defs.ShipBlueprints[0];

                // The game rejects thumbnails >= 1MB with an obscure box.
                string thumb = Path.Combine(localDir, "thumb.png");
                if (File.Exists(thumb) && new FileInfo(thumb).Length >= MaxThumbBytes)
                { ShipyardRunner.ShowMessage("The thumbnail is over " + (MaxThumbBytes / 1_000_000) + " MB - Steam rejects it.\nRe-save the blueprint (Ctrl+B) to regenerate it."); return; }

                var map = LoadMap();
                ulong existing;
                map.TryGetValue(mapKey, out existing);

                var ugc = MyGameService.GetDefaultUGC();
                if (ugc == null)
                { ShipyardRunner.ShowMessage("Steam Workshop isn't available (game not launched through Steam)."); return; }
                string service = ugc.ServiceName;
                var ids = new[] { new WorkshopId(existing, service) };
                string fallbackTitle = mapKey.Contains("/") ? mapKey.Substring(mapKey.LastIndexOf('/') + 1) : mapKey;
                string title = !string.IsNullOrEmpty(bp.CubeGrids[0].DisplayName) ? bp.CubeGrids[0].DisplayName : fallbackTitle;

                // Tag exactly like the game's own publish (type + safety + grid size).
                var tags = new List<string> { "blueprint", MySteamConstants.TAG_SAFE,
                    bp.CubeGrids[0].GridSizeEnum == MyCubeSize.Large ? "large_grid" : "small_grid" };
                var dlcs = new HashSet<uint>();
                if (bp.DLCs != null)
                    foreach (var d in bp.DLCs)
                    {
                        VRage.Game.Definitions.MyDlcDefinition dlc; uint appId;
                        if (Sandbox.Game.MyDLCs.TryGetDLC(d, out dlc)) dlcs.Add(dlc.AppId);
                        else if (uint.TryParse(d, out appId)) dlcs.Add(appId);
                    }

                ShipyardRunner.Confirm("STEAM WORKSHOP",
                    existing != 0
                        ? "Update your existing Workshop item for '" + title + "'?\n(item id " + existing + " - same page, new version; keeps its current visibility)"
                        : "Publish '" + title + "' to the Steam Workshop as an UNLISTED item?\n" +
                          "(Only people with the link can see it - share it with your crew, or flip it\n" +
                          "to Public on its Workshop page. Pushing again later updates the same item.)",
                    ok =>
                    {
                        if (!ok) return;
                        // Publish ONLY the blueprint files. localDir also holds shipyard.meta (repo
                        // owner/name + the CODEOWNER's GitHub handle), and the game uploads the WHOLE
                        // folder (only executable extensions are filtered) - that would leak private
                        // identity to the public Workshop. Stage bp.sbc + thumb.png into a temp dir.
                        string stage = Path.Combine(Path.GetTempPath(), "ShipyardPub_" + Guid.NewGuid().ToString("N"));
                        try
                        {
                            Directory.CreateDirectory(stage);
                            File.Copy(bpPath, Path.Combine(stage, "bp.sbc"), true);
                            if (File.Exists(thumb)) File.Copy(thumb, Path.Combine(stage, "thumb.png"), true);
                        }
                        catch (Exception ex) { try { Directory.Delete(stage, true); } catch { /* temp staging dir may not exist / be locked; harmless, real error logged next */ } Plugin.Log("workshop stage failed: " + ex.Message); ShipyardRunner.ShowMessage("Couldn't prepare the upload: " + ex.Message); return; }
                        // The game shows its own progress overlay + result box.
                        // Unlisted by default: visible by link only, never in Workshop browse/search.
                        try
                        {
                        MyWorkshop.PublishBlueprintAsync(stage, title, bp.Description ?? "",
                            ids, tags.ToArray(), dlcs.Count > 0 ? new List<uint>(dlcs).ToArray() : null,
                            MyPublishedFileVisibility.Unlisted,
                            (success, result, resultService, publishedItems) =>
                            {
                                ShipyardRunner.InvokeOnMain(() =>
                                {
                                    try { Directory.Delete(stage, true); } catch { }   // best-effort: remove the temp upload staging dir (a leftover is harmless if locked)
                                    if (success && publishedItems != null && publishedItems.Length > 0)
                                    {
                                        ulong id = publishedItems[0].Id;
                                        var m2 = LoadMap();
                                        m2[mapKey] = id;
                                        SaveMap(m2);
                                        // If we expected an in-place update (existing != 0) but the game
                                        // returned a different id, the old item was deleted/unowned and the
                                        // game created a NEW unlisted item instead - surface that in the log.
                                        if (existing != 0 && id != existing)
                                            Plugin.Log("workshop publish: expected update of item " + existing + " but got NEW item " + id +
                                                       " (old item likely deleted or not owned) for " + mapKey);
                                        Plugin.Log("workshop publish ok: " + mapKey + " -> " + id);
                                        string url = "https://steamcommunity.com/sharedfiles/filedetails/?id=" + id;
                                        // Open the item's page right after publish
                                        try { MyGameService.OpenOverlayUrl(url); }
                                        catch (Exception ex) { Plugin.Log("OpenOverlayUrl failed: " + ex.Message); }
                                        // Explicit confirmation (the URL is also the fallback if the Steam
                                        // overlay is disabled, e.g. launched outside Steam).
                                        ShipyardRunner.ShowResult(
                                            (existing != 0 ? "Workshop item UPDATED:  " : "Published to the Workshop:  ") + title + "\n" +
                                            "item " + id + "  -  the new version is live (opening its page).\n" +
                                            url + "\n\n" +
                                            "(Steam shows updates as blank entries under Change Notes - the game's\n" +
                                            "publish pipeline can't attach note text; check the 'Updated' date instead.)");
                                    }
                                    else
                                        // Failure: the game's reporter has the good error texts
                                        // (incl. the 'accept the Workshop agreement' case).
                                        MyWorkshop.ReportPublish(publishedItems, result, resultService);
                                });
                            });
                        }
                        catch (Exception ex)
                        {
                            // Synchronous failure before the callback fires: clean up the staging dir
                            // ourselves, since the callback (which normally deletes it) won't run.
                            try { Directory.Delete(stage, true); } catch { /* best-effort; a leftover temp dir is harmless */ }
                            Plugin.Log("workshop publish failed: " + ex.Message);
                            ShipyardRunner.ShowMessage("Workshop publish failed: " + ex.Message);
                        }
                    });
            }
            catch (Exception ex)
            {
                Plugin.Log("workshop push failed: " + ex);
                ShipyardRunner.ShowMessage("Workshop publish failed: " + ex.Message);
            }
        }
    }
}
