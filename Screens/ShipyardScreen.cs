using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace ShipyardPlugin
{
    // One tabbed Shipyard screen: Browse (install/diff/delete), Update (update-existing / publish-new),
    // and Review (open PRs). Positions are relative to the panel so it scales with resolution.
    public class ShipyardScreen : MyGuiScreenDebugBase
    {
        public enum Tab { Browse, Update, Review }

        private List<ShipEntry> _ships;
        private List<PrEntry> _prs;

        private Tab _tab;
        private string _pendingSearch;
        private string _folder;               // Browse: current folder path under the top folder; null/"" = root

        private MyGuiControlRadioButtonGroup _shipGroup;  // Browse + Update ship selection
        private MyGuiControlRadioButtonGroup _prGroup;    // Review PR selection
        private MyGuiControlTextbox _search;

        private enum UpdateMode { PickLocal, Checkouts, Workshop }
        private UpdateMode _updateMode = UpdateMode.PickLocal;
        private MyGuiControlRadioButtonGroup _localGroup;
        // Local-blueprint library scan is expensive (disk enumeration + thumbnails). Cache it for the life
        // of the screen and reuse across RecreateControls; invalidated only on OnRefresh (like _ships/_prs).
        private List<ShipyardApi.LocalBp> _localBpCache;

        // The currently-open Shipyard screen, for the auto-refresh hook (ShipyardApi calls RefreshActive
        // after a repo-changing op). Guarded by IsOpened; cleared on close.
        private static ShipyardScreen _active;

        // ---- animation (per-frame Update) ----
        private const float FlashFadeTicks = 32f;   // tab-switch flash fades to invisible over this many ticks
        private int _tick, _flashTick;
        private MyGuiControlLabel _flash;   // "channel switch" flash on tab change
        private MyGuiControlLabel _link;    // blinking link indicator
        private string _linkBase = "MANDATE LINK ONLINE";   // text the link indicator blinks (online/offline)
        private MyGuiControlLabel _status;  // non-modal background-op / sync status
        private string _statusText = "";    // survives the RecreateControls of a tab switch

        // Is a Shipyard menu currently on screen? (so ops can show in-menu status vs a HUD note)
        public static bool IsActiveOpen => _active != null && _active.IsOpened;

        // Set/clear the in-menu status line (e.g. "syncing...", "installing..."). No-op if closed.
        public static void SetStatus(string text)
        {
            var s = _active;
            if (s == null || !s.IsOpened) return;
            s._statusText = text ?? "";
            if (s._status != null) { s._status.Text = s._statusText; s._status.Visible = !string.IsNullOrEmpty(s._statusText); }
        }

        public ShipyardScreen(List<ShipEntry> ships, List<PrEntry> prs, string localName, Tab tab = Tab.Browse, string folder = null)
            // NOT top-most: the menu now stays open during actions, so result boxes / confirm dialogs
            // (which are themselves top-most) must be able to layer ABOVE it.
            : base(new Vector2(0.5f, 0.5f), new Vector2(0.62f, 0.97f),
                   Brand.Bg, isTopMostScreen: false)
        {
            _ships = ships ?? new List<ShipEntry>();
            _prs = prs ?? new List<PrEntry>();
            // localName (F10-selected local blueprint) is accepted for call-site compatibility but is
            // not currently used by this screen; the Publish picker enumerates the local library itself.
            _ = localName;
            _tab = tab;
            _folder = folder;
            // The Update tab opens on the update flow (your checked-out ships) online; offline there are no
            // checkouts, so it opens on the new-ship picker. Publishing a new ship is a button from there.
            _updateMode = Auth.IsOffline ? UpdateMode.PickLocal : UpdateMode.Checkouts;
            _active = this;
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "ShipyardScreen";

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);

            // OFFLINE: no Review tab (PRs are a GitHub thing). Fall back to Browse if it was selected.
            if (Auth.IsOffline && _tab == Tab.Review) _tab = Tab.Browse;

            // The tab row IS the header (a caption here would overlap the tabs given the panel height).
            if (Auth.IsOffline)
            {
                BuildTab("Browse", Tab.Browse, -0.1f);
                BuildTab("Publish", Tab.Update, 0.1f);
            }
            else
            {
                BuildTab("Browse", Tab.Browse, -0.205f);
                BuildTab("Update", Tab.Update, 0.0f);
                BuildTab("Review", Tab.Review, 0.205f);
            }

            switch (_tab)
            {
                case Tab.Browse: BuildBrowse(); break;
                case Tab.Update: BuildUpdate(); break;
                case Tab.Review: BuildReview(); break;
            }

            // ---- footer ---- (kept inside the panel bg)
            if (_tab != Tab.Browse)
            {
                // Both built with MakeButton (same control type/origin) so they sit on EXACTLY the same row.
                MakeButton("Highlight Options", new Vector2(-0.135f, 0.40f), new Vector2(0.26f, 0.04f),
                    (Action<MyGuiControlButton>)(b => ShipyardApi.OpenHighlightOptions()));
                MakeButton("Close", new Vector2(0.135f, 0.40f), new Vector2(0.26f, 0.04f),
                    (Action<MyGuiControlButton>)(b => CloseScreen(false)));
            }
            else
            {
                var close = AddButton("Close", (Action<MyGuiControlButton>)(b => CloseScreen(false)), null, null);
                close.Position = new Vector2(0f, 0.415f);
            }
            Controls.Add(Frame.MakeFooter(0.462f));   // classification strip

            // ---- animated bits: blinking link indicator + a brief tab-switch "channel" flash ----
            _linkBase = Auth.IsOffline ? "LOCAL  //  OFFLINE" : "MANDATE LINK ONLINE";
            _link = new MyGuiControlLabel(new Vector2(0.30f, -0.355f), null, "* " + _linkBase,
                Auth.IsOffline ? Brand.Warn : Brand.AccentDim, 0.6f)
            { OriginAlign = VRage.Utils.MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER };
            Controls.Add(_link);

            // Non-modal status line (background sync / in-menu op feedback) instead of a loading overlay.
            _status = new MyGuiControlLabel(new Vector2(0.30f, -0.33f), null, _statusText, Brand.Accent, 0.62f)
            { OriginAlign = VRage.Utils.MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER, Visible = !string.IsNullOrEmpty(_statusText) };
            Controls.Add(_status);

            _flash = new MyGuiControlLabel(new Vector2(0f, -0.055f), null,
                ">>>   " + _tab.ToString().ToUpperInvariant() + "   <<<", Brand.Accent, 1.4f)
            { OriginAlign = VRage.Utils.MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER };
            Controls.Add(_flash);
            _flashTick = 0;
        }

        public override bool Update(bool hasFocus)
        {
            _tick++; _flashTick++;
            // The blink only flips every 30 ticks; the flash only animates for its fade window.
            if (_link != null && _tick % 30 == 0)
                _link.Text = ((_tick / 30) % 2 == 0 ? "* " : "  ") + _linkBase;
            if (_flash != null && _flashTick <= FlashFadeTicks)
            {
                float a = 1f - Math.Min(1f, _flashTick / FlashFadeTicks);   // fade out over ~0.5s
                if (a <= 0.02f) _flash.Visible = false;
                else { var c = Brand.Accent; c.W = a; _flash.ColorMask = c; _flash.Visible = true; }
            }
            return base.Update(hasFocus);
        }

        // Same three utility buttons in the same spots on every tab, aligned under the tab row.
        private void BuildUtilityRow()
        {
            MakeButton("Refresh", new Vector2(-0.205f, -0.39f), new Vector2(0.19f, 0.04f),
                (Action<MyGuiControlButton>)(b => OnRefresh()));
            MakeButton("Pull ALL", new Vector2(0f, -0.39f), new Vector2(0.19f, 0.04f),
                (Action<MyGuiControlButton>)(b => ShipyardApi.InstallAll()));
            MakeButton("Account / Repo", new Vector2(0.205f, -0.39f), new Vector2(0.19f, 0.04f),
                (Action<MyGuiControlButton>)(b => { CloseScreen(false); ShipyardApi.OpenSettings(); }));
            // a rule under the shared control row, on every tab
            Controls.Add(Frame.MakeDivider(-0.368f, 0.56f));
        }

        // A fixed-size button. AddButton's debug-style buttons ignore Size (they overlap), so for
        // anything that must sit side-by-side we build a Rectangular button with an explicit width.
        private MyGuiControlButton MakeButton(string text, Vector2 pos, Vector2 size, Action<MyGuiControlButton> onClick)
        {
            var b = Frame.MakeButton(text, pos, size, onClick);
            Controls.Add(b);
            return b;
        }

        private void BuildTab(string label, Tab tab, float x)
        {
            var b = MakeButton(_tab == tab ? "[ " + label + " ]" : label,
                new Vector2(x, -0.44f), new Vector2(0.19f, 0.045f),
                (Action<MyGuiControlButton>)(_ => SwitchTo(tab)));
            // The light-blue highlight follows keyboard FOCUS, so focus the active tab (otherwise the
            // first-created button, Browse, looks selected on every tab). The brackets are the backup cue.
            if (_tab == tab) FocusedControl = b;
        }

        private void SwitchTo(Tab tab)
        {
            SaveInputs();
            _tab = tab;
            RecreateControls(false);
        }

        // Re-fetch ships + PRs without leaving the screen, then rebuild the current tab.
        private void OnRefresh()
        {
            SaveInputs();
            ShipyardApi.RefreshData(data =>
            {
                if (data == null) return;   // match ApplyData's contract: treat a null payload as a no-op
                _ships = data.Ships ?? _ships;
                _prs = data.Prs ?? _prs;
                _localBpCache = null;       // force a fresh local-library scan on the next Update/Publish build
                RecreateControls(false);
            });
        }

        // Rebuild the open menu with freshly-fetched data after a repo op completes (called on the main
        // thread from RunRepoOp). Guarded by IsOpened so it never touches a closed screen.
        public static void ApplyData(ShipyardData data)
        {
            var s = _active;
            if (s == null || !s.IsOpened || data == null) return;
            s._ships = data.Ships ?? s._ships;
            s._prs = data.Prs ?? s._prs;
            try { s.RecreateControls(false); } catch (Exception ex) { Plugin.Log("apply data failed: " + ex.Message); }
        }

        protected override void OnClosed()
        {
            if (_active == this) _active = null;
            base.OnClosed();
        }

        // Close an already-open Shipyard screen before opening a new one, so views never stack.
        public static void CloseActiveIfOpen()
        {
            var s = _active;
            if (s != null && s.IsOpened) { try { s.CloseScreen(false); } catch (Exception ex) { Plugin.Log("close active screen failed: " + ex.Message); } }
            _active = null;
        }

        // Preserve user-typed text across the rebuild that tab switches trigger.
        private void SaveInputs()
        {
            if (_search != null) _pendingSearch = _search.Text;
        }

        // Single definition of how the pending search text is normalized, shared by every tab.
        private string SearchQuery() => (_pendingSearch ?? "").Trim();
        private string SearchQueryLower() => SearchQuery().ToLowerInvariant();

        // Stack action buttons vertically at column x
        private void StackButtons(List<MyGuiControlButton> buttons, float startY, float x = 0f)
        {
            float y = startY;
            foreach (var b in buttons) { b.Position = new Vector2(x, y); y += b.Size.Y + 0.005f; }
        }

        // Shared search cluster: identical bar+button on every tab.
        private const float SearchY = -0.2925f;
        private void BuildSearchCluster()
        {
            _search = new MyGuiControlTextbox();
            _search.Position = new Vector2(-0.075f, SearchY);
            _search.Size = new Vector2(0.41f, 0.035f);
            if (!string.IsNullOrEmpty(_pendingSearch)) _search.Text = _pendingSearch;
            Controls.Add(_search);
            MakeButton("Search", new Vector2(0.205f, SearchY + 0.0025f), new Vector2(0.13f, 0.035f),
                (Action<MyGuiControlButton>)(b => { SaveInputs(); RecreateControls(false); }));
        }

        // =====================================================================  BROWSE
        private void BuildBrowse()
        {
            BuildUtilityRow();

            // Breadcrumb: search results > inside-a-folder > root folder list.
            string q = SearchQuery();
            string root = Auth.RootFolder;
            string crumb = q.Length > 0 ? "Search: \"" + q + "\"  (all folders)"
                         : string.IsNullOrEmpty(_folder) ? root + " /   (open a folder)"
                         : root + " / " + _folder;
            var crumbLbl = AddLabel(crumb, Brand.Accent, 0.85f, null, null);
            crumbLbl.Position = new Vector2(-0.28f, -0.35f);

            BuildSearchCluster();

            var list = new MyGuiControlList(new Vector2(0f, -0.11f), new Vector2(0.58f, 0.29f),
                null, null, MyGuiControlListStyleEnum.Default);
            _shipGroup = BuildShipTiles(list, _pendingSearch, true);
            Controls.Add(list);

            // Two columns: get-a-copy actions on the left, collaborative-work actions on the right.
            string navLabel = string.IsNullOrEmpty(_folder) ? "Open Folder" : "Open Folder / Up";
            var size = new Vector2(0.245f, 0.034f);
            var left = new List<MyGuiControlButton>
            {
                MakeButton(navLabel, Vector2.Zero, size, (Action<MyGuiControlButton>)(x => OnNav())),
                MakeButton("Details / Specs", Vector2.Zero, size, (Action<MyGuiControlButton>)(x => OnDetails())),
                MakeButton("Install to Library", Vector2.Zero, size, (Action<MyGuiControlButton>)(x => OnInstall())),
                MakeButton("Load to Clipboard", Vector2.Zero, size, (Action<MyGuiControlButton>)(x => OnLoadClipboard())),
                MakeButton("Highlight diff", Vector2.Zero, size, (Action<MyGuiControlButton>)(x => OnHighlight())),
                MakeButton("Highlight Options", Vector2.Zero, size, (Action<MyGuiControlButton>)(x => ShipyardApi.OpenHighlightOptions())),
            };
            if (Auth.IsOffline)
            {
                // Offline: no checkout lock / PR / review. Updating = commit the looked-at grid straight
                // to local main (select the ship, aim at your edited grid). Left column only, centered.
                left.Add(MakeButton("Commit changes (to this ship)", Vector2.Zero, size,
                    (Action<MyGuiControlButton>)(x => OnLocalCommit())));
                StackButtons(left, 0.075f, 0f);
            }
            else
            {
                var right = new List<MyGuiControlButton>
                {
                    MakeButton("Check Out (lock+paste)", Vector2.Zero, size, (Action<MyGuiControlButton>)(x => OnCheckOut())),
                    MakeButton("Commit looked-at ship", Vector2.Zero, size, (Action<MyGuiControlButton>)(x => OnCommit())),
                    MakeButton("Open update PR", Vector2.Zero, size, (Action<MyGuiControlButton>)(x => OnFinishCheckout())),
                    MakeButton("Release checkout", Vector2.Zero, size, (Action<MyGuiControlButton>)(x => OnRelease())),
                    MakeButton("Push to Steam Workshop", Vector2.Zero, size, (Action<MyGuiControlButton>)(x => OnWorkshopPushRepo())),
                    MakeButton("Delete from Shipyard", Vector2.Zero, size, (Action<MyGuiControlButton>)(x => OnDelete())),
                };
                StackButtons(left, 0.095f, -0.1275f);
                StackButtons(right, 0.095f, 0.1275f);
            }
        }

        // =====================================================================  UPDATE
        // Default (online): the update flow - your checked-out ships, publish their update PRs. Buttons
        // branch to the new-ship publish picker and the Steam Workshop push. Offline: new-ship picker only.
        private void BuildUpdate()
        {
            BuildUtilityRow();
            if (_updateMode == UpdateMode.Checkouts) { BuildUpdateCheckouts(); return; }
            if (_updateMode == UpdateMode.Workshop) { BuildUpdateWorkshop(); return; }

            var hdr = AddLabel("PUBLISH  -  a NEW ship from a local blueprint", Brand.Accent, 0.85f, null, null);
            hdr.Position = new Vector2(-0.28f, -0.35f);

            var locals = _localBpCache ?? (_localBpCache = ShipyardApi.LocalBlueprintsDetailed());

            // Search over the local library
            BuildSearchCluster();

            string q = SearchQueryLower();
            var shown = q.Length > 0 ? locals.Where(lb => lb.Name.ToLowerInvariant().Contains(q)).ToList() : locals;
            if (shown.Count == 0)
            {
                string msg = locals.Count == 0 ? "No local blueprints found - save one with Ctrl+B first."
                                               : "No local blueprint matches '" + _pendingSearch.Trim() + "'.";
                var none = new MyGuiControlLabel(new Vector2(0f, -0.1f), null, msg)
                { OriginAlign = VRage.Utils.MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER };
                Controls.Add(none);
            }
            else
            {
                var list = new MyGuiControlList(new Vector2(0f, -0.11f), new Vector2(0.58f, 0.29f),
                    null, null, MyGuiControlListStyleEnum.Default);
                _localGroup = BuildLocalTiles(list, shown);
                Controls.Add(list);
            }

            var buttons = new List<MyGuiControlButton>
            {
                MakeButton(Auth.IsOffline ? "Save as a NEW ship (local commit)" : "Publish as NEW ship",
                    Vector2.Zero, new Vector2(0.5f, 0.04f), (Action<MyGuiControlButton>)(b => OnPublishNew())),
            };
            // Online: this is the secondary view (publishing a brand-new ship); the tab default is the
            // update flow, so offer a way back. Offline has no update flow, so this IS the only view.
            if (!Auth.IsOffline)
                buttons.Add(MakeButton("<  Back to updates", Vector2.Zero, new Vector2(0.5f, 0.04f),
                    (Action<MyGuiControlButton>)(b => { _updateMode = UpdateMode.Checkouts; SaveInputs(); RecreateControls(false); })));
            StackButtons(buttons, 0.10f);
        }

        // Workshop push view: pick a SHIPYARD ship (repo-ships-only). The push syncs the local copy
        // from main before publishing, so the Workshop item always matches the repo.
        private void BuildUpdateWorkshop()
        {
            var hdr = AddLabel("STEAM WORKSHOP  -  push a shipyard ship", Brand.Accent, 0.85f, null, null);
            hdr.Position = new Vector2(-0.28f, -0.35f);

            BuildSearchCluster();

            var list = new MyGuiControlList(new Vector2(0f, -0.11f), new Vector2(0.58f, 0.29f),
                null, null, MyGuiControlListStyleEnum.Default);
            _shipGroup = BuildShipTiles(list, _pendingSearch, false);   // flat, searchable repo-ship picker
            Controls.Add(list);

            if (_ships.Count == 0)
            {
                var none = new MyGuiControlLabel(new Vector2(0f, -0.1f), null, "No ships in the shipyard yet.")
                { OriginAlign = VRage.Utils.MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER };
                Controls.Add(none);
            }

            var buttons = new List<MyGuiControlButton>
            {
                MakeButton("Sync from shipyard + Push to Workshop", Vector2.Zero, new Vector2(0.5f, 0.04f),
                    (Action<MyGuiControlButton>)(b => OnWorkshopPushRepo())),
                MakeButton("<  Back to updates", Vector2.Zero, new Vector2(0.5f, 0.04f),
                    (Action<MyGuiControlButton>)(b => { _updateMode = UpdateMode.Checkouts; SaveInputs(); RecreateControls(false); })),
            };
            StackButtons(buttons, 0.10f);
        }

        private void OnWorkshopPushRepo()
        {
            var sel = SelectedShip();
            if (sel == null) { ShipyardRunner.ShowMessage("Select a shipyard ship to push first."); return; }
            ShipyardApi.PushRepoShipToWorkshop(sel.CategoryShip);
        }

        // Update flow: a grid of the ships YOU currently have checked out. Selecting one publishes its PR
        private void BuildUpdateCheckouts()
        {
            var hdr = AddLabel("UPDATE  -  publish one of YOUR checked-out ships", Brand.Accent, 0.85f, null, null);
            hdr.Position = new Vector2(-0.28f, -0.35f);

            string me = Auth.Login;
            var mine = _ships.Where(s => s.CheckedOutBy != null && string.Equals(s.CheckedOutBy, me, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(s => s.CategoryShip, StringComparer.OrdinalIgnoreCase).ToList();

            if (mine.Count == 0)
            {
                var none = new MyGuiControlLabel(new Vector2(0f, -0.12f), null,
                    "You have no checked-out ships. Check one out from Browse, build it,\n'Commit looked-at ship' (or /sy commit), then come back here to publish the update.")
                { OriginAlign = VRage.Utils.MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER };
                Controls.Add(none);
            }
            else
            {
                var list = new MyGuiControlList(new Vector2(0f, -0.11f), new Vector2(0.58f, 0.29f),
                    null, null, MyGuiControlListStyleEnum.Default);
                _shipGroup = BuildCheckoutTiles(list, mine);
                Controls.Add(list);
            }

            var buttons = new List<MyGuiControlButton>
            {
                MakeButton("Publish Update (open PR)", Vector2.Zero, new Vector2(0.5f, 0.04f), (Action<MyGuiControlButton>)(b => OnPublishUpdate())),
                MakeButton("Release Checkout (delete branch)", Vector2.Zero, new Vector2(0.5f, 0.04f), (Action<MyGuiControlButton>)(b => OnReleaseUpdate())),
                MakeButton("Publish a new ship to Shipyard  >", Vector2.Zero, new Vector2(0.5f, 0.04f),
                    (Action<MyGuiControlButton>)(b => { _updateMode = UpdateMode.PickLocal; SaveInputs(); RecreateControls(false); })),
                // Steam Workshop push is always available (repo-ships-only, synced from main before pushing).
                MakeButton("Publish a ship to Steam Workshop  >", Vector2.Zero, new Vector2(0.5f, 0.04f),
                    (Action<MyGuiControlButton>)(b => { _updateMode = UpdateMode.Workshop; SaveInputs(); RecreateControls(false); })),
            };
            StackButtons(buttons, 0.10f);
        }

        // Thumbnail tiles for the checked-out-ships grid
        private MyGuiControlRadioButtonGroup BuildCheckoutTiles(MyGuiControlList list, List<ShipEntry> ships)
        {
            var group = new MyGuiControlRadioButtonGroup();
            var tiles = new List<MyGuiControlBase>();
            int key = 0;
            foreach (var e in ships)
            {
                var tile = new MyGuiControlContentButton(e.CategoryShip, e.ThumbPath ?? "", null) { Key = key++, UserData = e };
                group.Add(tile); tiles.Add(tile);
            }
            FillGrid(list, tiles);
            return group;
        }

        // Local-blueprint tiles (thumbnail + name) for the Publish grid; UserData is the LocalBp.
        private MyGuiControlRadioButtonGroup BuildLocalTiles(MyGuiControlList list, List<ShipyardApi.LocalBp> locals)
        {
            var group = new MyGuiControlRadioButtonGroup();
            var tiles = new List<MyGuiControlBase>();
            int key = 0;
            foreach (var lb in locals)
            {
                var tile = new MyGuiControlContentButton(lb.Name, lb.ThumbPath ?? "", null) { Key = key++, UserData = lb };
                group.Add(tile); tiles.Add(tile);
            }
            FillGrid(list, tiles);
            return group;
        }

        private ShipyardApi.LocalBp SelectedLocalBp() =>
            _localGroup != null && _localGroup.SelectedButton != null ? _localGroup.SelectedButton.UserData as ShipyardApi.LocalBp : null;

        private void OnPublishNew()
        {
            var lb = SelectedLocalBp();
            if (lb == null) { ShipyardRunner.ShowMessage("Select a local blueprint first."); return; }
            MyGuiSandbox.AddScreen(new PublishNewScreen(lb.Name));
        }

        private void OnPublishUpdate()
        {
            var sel = SelectedShip();
            if (sel == null) { ShipyardRunner.ShowMessage("Select a checked-out ship first."); return; }
            ShipyardRunner.Confirm("PUBLISH UPDATE",
                "Open a PR to publish your checkout of '" + sel.CategoryShip + "' to main?\n" +
                "(It uses the latest commit on your work branch - commit your changes first if you haven't.)",
                ok => { if (ok) ShipyardApi.FinishCheckout(sel); });
        }

        private void OnReleaseUpdate()
        {
            var sel = SelectedShip();
            if (sel == null) { ShipyardRunner.ShowMessage("Select a checked-out ship first."); return; }
            ShipyardRunner.Confirm("RELEASE CHECKOUT",
                "Delete the work branch for '" + sel.CategoryShip + "'?\n" +
                "Committed WIP on it is LOST (anything already merged to main is safe).",
                ok => { if (ok) ShipyardApi.ReleaseCheckout(sel); });
        }

        // =====================================================================  REVIEW
        private void BuildReview()
        {
            BuildUtilityRow();

            var hdr = AddLabel("REVIEW  -  open pull requests", Brand.Accent, 0.85f, null, null);
            hdr.Position = new Vector2(-0.28f, -0.35f);

            // Same search cluster as Browse/Publish (UI consistency): filters by ship, title, author, #.
            BuildSearchCluster();
            string q = SearchQueryLower();
            var shown = q.Length == 0 ? _prs : _prs.Where(pr =>
                   (pr.CategoryShip ?? "").ToLowerInvariant().Contains(q)
                || (pr.Title ?? "").ToLowerInvariant().Contains(q)
                || (pr.Author ?? "").ToLowerInvariant().Contains(q)
                || ("#" + pr.Number).Contains(q)).ToList();

            _prGroup = new MyGuiControlRadioButtonGroup();
            var tiles = new List<MyGuiControlBase>();
            int key = 0;
            string lastGroup = null;
            foreach (var pr in shown)
            {
                string grp = pr.Group();
                string prefix = grp != lastGroup ? "[" + grp.ToUpperInvariant() + "]  " : "";
                lastGroup = grp;
                string shipName = pr.CategoryShip != null && pr.CategoryShip.Contains("/")
                    ? pr.CategoryShip.Substring(pr.CategoryShip.LastIndexOf('/') + 1) : (pr.CategoryShip ?? pr.Title);
                string title = prefix + shipName + "   (" + pr.Status() + ")   #" + pr.Number + " by @" + pr.Author;
                var tile = new MyGuiControlContentButton(title, pr.ThumbPath ?? "", null) { Key = key++, UserData = pr };
                _prGroup.Add(tile);
                tiles.Add(tile);
            }
            var list = new MyGuiControlList(new Vector2(0f, -0.11f), new Vector2(0.58f, 0.29f),
                null, null, MyGuiControlListStyleEnum.Default);
            FillGrid(list, tiles);
            Controls.Add(list);

            if (shown.Count == 0)
            {
                var lbl = new MyGuiControlLabel(new Vector2(0f, -0.15f), null,
                    _prs.Count == 0 ? "(no open pull requests)" : "(no PR matches '" + _pendingSearch.Trim() + "')")
                {
                    OriginAlign = VRage.Utils.MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER
                };
                Controls.Add(lbl);
            }

            var buttons = new List<MyGuiControlButton>
            {
                MakeButton("Open / Details", Vector2.Zero, new Vector2(0.5f, 0.04f), (Action<MyGuiControlButton>)(b => ActPr(pr => ShipyardApi.ShowPrDetails(pr)))),
                MakeButton("Visual Diff (spawn in world)", Vector2.Zero, new Vector2(0.5f, 0.04f), (Action<MyGuiControlButton>)(b => ActPrInWorld(pr => ShipyardApi.VisualDiff(pr)))),
                MakeButton("Install this version", Vector2.Zero, new Vector2(0.5f, 0.04f), (Action<MyGuiControlButton>)(b => ActPr(pr => ShipyardApi.InstallPrVersion(pr), needsShip: true))),
                MakeButton("Approve", Vector2.Zero, new Vector2(0.5f, 0.04f), (Action<MyGuiControlButton>)(b => ActPr(pr => ShipyardApi.Approve(pr.Number)))),
                MakeButton("Request Changes", Vector2.Zero, new Vector2(0.5f, 0.04f), (Action<MyGuiControlButton>)(b => ActPr(pr => ShipyardApi.RequestChanges(pr.Number)))),
                MakeButton("Merge", Vector2.Zero, new Vector2(0.5f, 0.04f), (Action<MyGuiControlButton>)(b => ActPr(pr => ShipyardApi.Merge(pr)))),
                MakeButton("Reject PR", Vector2.Zero, new Vector2(0.5f, 0.04f), (Action<MyGuiControlButton>)(b => ConfirmClosePr())),
            };
            StackButtons(buttons, 0.07f);
        }

        // ---- multi-column grid: MyGuiControlList is single-column, so pack tiles into a row-container
        // per row and let the list scroll by rows. Uses the panel's horizontal space. ----
        private const string FolderIconTex = "Textures\\GUI\\Icons\\Blueprints\\FolderIcon.png";
        // Sentinel that marks a folder tile's UserData (vs a ShipEntry). The path segment follows the prefix.
        private const string DirPrefix = "DIR:";
        private static bool _styleTileLogged;   // throttle StyleTile failure logging to once per session
        private const float TileGap = 0.012f;
        private void FillGrid(MyGuiControlList list, List<MyGuiControlBase> tiles)
        {
            bool allFolders = tiles.Count > 0 && tiles.All(t =>
                t is MyGuiControlContentButton && t.UserData is string s && s.StartsWith(DirPrefix));
            int cols = allFolders ? 5 : 3;
            float tileH = allFolders ? 0.115f : 0.14f;   // folder cell holds a box-filling glyph + label;
                                                         // ship cell hugs the aspect-correct thumbnail + label

            float listW = list.Size.X;
            // Reserve room for the scrollbar + item margins + the observed rightward render bias so the
            // last column never clips. (Calibrated below; conservative so it always fits.)
            float usableW = listW - 0.09f;
            float tileW = (usableW - (cols - 1) * TileGap) / cols;
            float step = tileW + TileGap;
            float c0 = -(cols - 1) / 2f * step;   // centered: cols at c0, c0+step, ...
            var rows = new List<MyGuiControlBase>();
            for (int i = 0; i < tiles.Count; i += cols)
            {
                var row = new MyGuiControlParent(size: new Vector2(listW, tileH));
                for (int c = 0; c < cols && i + c < tiles.Count; c++)
                {
                    var t = tiles[i + c];
                    StyleTile(t, tileW, tileH, allFolders);
                    t.Position = new Vector2(c0 + c * step, -tileH / 2f);
                    row.Controls.Add(t);
                }
                rows.Add(row);
            }
            list.InitControls(rows);
            CenterGrid(list, rows);
        }

        private void CenterGrid(MyGuiControlList list, List<MyGuiControlBase> rows)
        {
            try
            {
                if (rows.Count == 0) return;
                var listTL = list.GetPositionAbsoluteTopLeft();
                var itemsRect = (RectangleF)Traverse.Create(list).Field("m_itemsRectangle").GetValue();
                float contentCenter = listTL.X + itemsRect.X + itemsRect.Width / 2f;

                var firstRow = rows[0] as MyGuiControlParent;
                if (firstRow == null) return;
                var t0 = firstRow.Controls.FirstOrDefault();
                if (t0 == null) return;
                // abs grid-center = tile abs-center minus its local offset (rows are symmetric around 0).
                float gridCenter = t0.GetPositionAbsoluteCenter().X - t0.Position.X;
                float shift = contentCenter - gridCenter;

                foreach (var r in rows)
                {
                    var rp = r as MyGuiControlParent;
                    if (rp == null) continue;
                    foreach (var t in rp.Controls)
                        t.Position = new Vector2(t.Position.X + shift, t.Position.Y);
                }
            }
            catch (Exception ex) { Plugin.Log("center grid failed: " + ex.Message); }
        }

        private static float SafeGuiAspect()
        {
            try { var r = MyGuiManager.GetSafeGuiRectangle(); if (r.Height > 0) return (float)r.Width / r.Height; }
            catch { }   // GUI rectangle unavailable -> fall back to the 4:3 default below
            return 4f / 3f;
        }

        // Read a PNG's aspect ratio (w/h) from its IHDR header without decoding the image. Returns 16:9 if
        // the file is missing/unreadable (e.g. the no-thumbnail placeholder).
        private static float PngAspect(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 16f / 9f;
                using (var fs = File.OpenRead(path))
                {
                    var b = new byte[24];
                    if (fs.Read(b, 0, 24) < 24) return 16f / 9f;
                    int w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                    int h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                    if (w > 0 && h > 0) return (float)w / h;
                }
            }
            catch { }   // missing/corrupt/unreadable PNG header -> 16:9 fallback (per this method's contract above)
            return 16f / 9f;
        }

        // Force a content-button tile to the grid cell size, overriding its built-in self-sizing. Folders
        // get a small icon (file-browser look); ships keep a thumbnail that fills the cell width. Order
        // matters: set the preview size first, THEN base.Size (which re-runs the button's UpdatePositions
        // and re-anchors the preview to the top-left for the new size).
        private void StyleTile(MyGuiControlBase tileBase, float tileW, float tileH, bool folder)
        {
            var tile = tileBase as MyGuiControlContentButton;
            if (tile == null) { tileBase.Size = new Vector2(tileW, tileH); return; }
            try
            {
                if (folder)
                {
                    // Square glyph that fills the cell (bounded by width and the height left under the label).
                    float s = Math.Min(tileW - 0.012f, tileH - 0.035f);
                    tile.m_previewImage.Size = new Vector2(s, s);
                }
                else
                {
                    float w = tileW - 0.012f;
                    float srcAR = PngAspect(tile.PreviewImagePath);
                    float h = w * SafeGuiAspect() / srcAR;
                    float maxH = tileH - 0.03f;   // leave room for the title label
                    if (h > maxH) { w *= maxH / h; h = maxH; }
                    tile.m_previewImage.Size = new Vector2(w, h);
                }
                // The ctor sized the label to the ORIGINAL (large) preview width; clamp it to the cell so
                // long names ellipsize inside their tile instead of bleeding into the next column.
                tile.m_titleLabel.SetMaxWidth(tileW - 0.008f);
            }
            catch (Exception ex)
            {
                // A broken publicized/internal name would otherwise spam the log for every tile every
                // rebuild; log once per session. The tile is still resized below (preview keeps default size).
                if (!_styleTileLogged) { _styleTileLogged = true; Plugin.Log("preview resize failed: " + ex.Message); }
            }
            tile.Size = new Vector2(tileW, tileH);
        }

        // ---- shared tile builder. folders=true mirrors the GitHub folder layout: category folders at
        // root, ships once inside one. A non-empty search query always flattens to matching ships. ----
        private MyGuiControlRadioButtonGroup BuildShipTiles(MyGuiControlList list, string query, bool folders)
        {
            string q = (query ?? "").Trim().ToLowerInvariant();
            var group = new MyGuiControlRadioButtonGroup();
            var tiles = new List<MyGuiControlBase>();
            int key = 0;

            if (folders && q.Length == 0)
            {
                // Tree view at the current folder: immediate subfolders (with recursive counts) + the
                // ships that live directly here. Works for arbitrary nesting depth.
                string cur = _folder ?? "";
                string prefix = cur.Length == 0 ? "" : cur + "/";
                var subCounts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in _ships)
                {
                    string folder = e.Folder ?? "";
                    string seg = null;
                    if (cur.Length == 0) { if (folder.Length > 0) seg = folder.Split('/')[0]; }
                    else if (folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && folder.Length > prefix.Length)
                        seg = folder.Substring(prefix.Length).Split('/')[0];
                    if (seg != null) subCounts[seg] = (subCounts.TryGetValue(seg, out var c) ? c : 0) + 1;
                }
                foreach (var kv in subCounts)
                {
                    var tile = new MyGuiControlContentButton(kv.Key + "   (" + kv.Value + ")", FolderIconTex, null)
                    { Key = key++, UserData = DirPrefix + kv.Key };
                    group.Add(tile); tiles.Add(tile);
                }
                foreach (var e in _ships.Where(e => string.Equals(e.Folder ?? "", cur, StringComparison.OrdinalIgnoreCase))
                                        .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var tile = new MyGuiControlContentButton(WipTag(e) + e.Name, e.ThumbPath ?? "", null) { Key = key++, UserData = e };
                    group.Add(tile); tiles.Add(tile);
                }
                FillGrid(list, tiles);
                Plugin.Log("browse /" + cur + ": " + subCounts.Count + " folders, " + (tiles.Count - subCounts.Count) + " ships");
                WireDoubleClick(group);
                return group;
            }

            // flat list (Publish) or search: ship tiles by full path
            var filtered = _ships.Where(e => q.Length == 0
                || e.CategoryShip.ToLowerInvariant().Contains(q)
                || (e.Tags != null && e.Tags.Any(t => t.ToLowerInvariant().Contains(q)))).ToList();
            foreach (var e in filtered)
            {
                var tile = new MyGuiControlContentButton(WipTag(e) + e.CategoryShip, e.ThumbPath ?? "", null) { Key = key++, UserData = e };
                group.Add(tile);
                tiles.Add(tile);
            }
            FillGrid(list, tiles);
            Plugin.Log("ship tiles: " + filtered.Count + "/" + _ships.Count + " (q='" + q + "')");
            WireDoubleClick(group);
            return group;
        }

        // Double-clicking a [DIR] tile opens that folder (the group's MouseDoubleClick is private -> Traverse).
        private void WireDoubleClick(MyGuiControlRadioButtonGroup group)
        {
            try { Traverse.Create(group).Field("MouseDoubleClick").SetValue((Action<MyGuiControlRadioButton>)OnTileDoubleClick); }
            catch (Exception ex) { Plugin.Log("wire dblclick failed: " + ex.Message); }
        }

        private void OnTileDoubleClick(MyGuiControlRadioButton rb)
        {
            var ud = rb != null ? rb.UserData : null;
            if (ud is string s && s.StartsWith(DirPrefix))
            {
                string seg = s.Substring(DirPrefix.Length);
                _folder = string.IsNullOrEmpty(_folder) ? seg : _folder + "/" + seg;
                SaveInputs();
                // Defer the rebuild: this fires WHILE the game is enumerating the radio-button group,
                // so rebuilding controls now throws "Collection was modified". Run it next frame.
                ShipyardRunner.InvokeOnMain(() => RecreateControls(false));
            }
            else if (ud is ShipEntry e)
            {
                // Double-click a ship -> its details/specs (deferred, same enumeration concern).
                ShipyardRunner.InvokeOnMain(() => ShipyardApi.ShowShipDetails(e));
            }
        }

        // ---- Details ----
        private void OnDetails()
        {
            var sel = SelectedShip();
            if (sel == null) { ShipyardRunner.ShowMessage("Select a ship first."); return; }
            ShipyardApi.ShowShipDetails(sel);
        }

        // "[WIP] " prefix for ships somebody has checked out (the lock is visible before clicking).
        private static string WipTag(ShipEntry e) => e.CheckedOutBy != null ? "[WIP] " : "";

        private ShipEntry SelectedShip() =>
            _shipGroup != null && _shipGroup.SelectedButton != null ? _shipGroup.SelectedButton.UserData as ShipEntry : null;

        // The selected tile's folder name, if a [DIR] folder tile is selected (else null).
        private string SelectedFolderName()
        {
            var ud = _shipGroup != null && _shipGroup.SelectedButton != null ? _shipGroup.SelectedButton.UserData as string : null;
            return ud != null && ud.StartsWith(DirPrefix) ? ud.Substring(DirPrefix.Length) : null;
        }

        // One nav button for the tree: open the selected subfolder, or go up a level if none is selected.
        private void OnNav()
        {
            string seg = SelectedFolderName();
            if (seg != null)
            {
                _folder = string.IsNullOrEmpty(_folder) ? seg : _folder + "/" + seg;
            }
            else if (!string.IsNullOrEmpty(_folder))
            {
                int sl = _folder.LastIndexOf('/');
                _folder = sl > 0 ? _folder.Substring(0, sl) : null;
            }
            else { ShipyardRunner.ShowMessage("Select a folder to open (or you're already at the root)."); return; }
            SaveInputs();
            RecreateControls(false);
        }
        private PrEntry SelectedPr() =>
            _prGroup != null && _prGroup.SelectedButton != null ? _prGroup.SelectedButton.UserData as PrEntry : null;

        // ---- Browse actions ----
        private void OnInstall()
        {
            var sel = SelectedShip();
            if (sel == null) { ShipyardRunner.ShowMessage("Select a ship first."); return; }
            ShipyardApi.Install(sel.CategoryShip);
        }

        // Native-style paste: the ship lands in the game clipboard attached to the cursor, the
        // player clicks to place it. Close the menu so they're back in-world for the placement.
        private void OnLoadClipboard()
        {
            var sel = SelectedShip();
            if (sel == null) { ShipyardRunner.ShowMessage("Select a ship first."); return; }
            if (MySession.Static == null) { ShipyardRunner.ShowMessage("Pasting requires being in a world."); return; }
            CloseScreen(false);
            ShipyardApi.LoadToClipboard(sel.CategoryShip);
        }

        // ---- checkout actions ----
        private void OnCheckOut()
        {
            var sel = SelectedShip();
            if (sel == null) { ShipyardRunner.ShowMessage("Select a ship first."); return; }
            if (MySession.Static == null) { ShipyardRunner.ShowMessage("Checkout requires being in a world (it pastes the ship)."); return; }
            ShipyardApi.CheckOut(sel);   // menu stays open; the WIP lands on the clipboard, close to place
        }

        private void OnCommit()
        {
            if (MySession.Static == null) { ShipyardRunner.ShowMessage("Committing requires being in a world."); return; }
            IMyCubeGrid grid;
            if (!ShipyardApi.TryGetLookedAtGrid(out grid))
            { ShipyardRunner.ShowMessage("Aim at the ship you're working on BEFORE opening the menu, then Commit."); return; }
            var sel = SelectedShip();
            // A selected tile is an explicit target; otherwise resolve against YOUR checkout(s).
            if (sel != null) ShipyardApi.CommitGrid(sel, grid);   // menu stays open; background upload
            else ShipyardApi.CommitResolved(null, grid);
        }

        // Offline update: select the repo ship, aim at your edited in-world grid, commit it to local main.
        private void OnLocalCommit()
        {
            var sel = SelectedShip();
            if (sel == null) { ShipyardRunner.ShowMessage("Select the ship you're updating first."); return; }
            if (MySession.Static == null) { ShipyardRunner.ShowMessage("Committing requires being in a world."); return; }
            IMyCubeGrid grid;
            if (!ShipyardApi.TryGetLookedAtGrid(out grid))
            { ShipyardRunner.ShowMessage("Aim at the ship you're updating BEFORE opening the menu, then Commit."); return; }
            ShipyardApi.LocalCommitGrid(sel.CategoryShip, grid);
        }

        private void OnFinishCheckout()
        {
            var sel = SelectedShip();
            if (sel == null) { ShipyardRunner.ShowMessage("Select a ship first."); return; }
            ShipyardApi.FinishCheckout(sel);
        }

        private void OnRelease()
        {
            var sel = SelectedShip();
            if (sel == null) { ShipyardRunner.ShowMessage("Select a ship first."); return; }
            ShipyardRunner.Confirm("RELEASE CHECKOUT",
                "Delete the work branch for '" + sel.CategoryShip + "'?\n" +
                "Committed WIP on it is LOST (merged work is safe on main).",
                ok => { if (ok) ShipyardApi.ReleaseCheckout(sel); });
        }

        private void OnHighlight()
        {
            var sel = SelectedShip();
            if (sel == null) { ShipyardRunner.ShowMessage("Select the repo ship to compare against first."); return; }
            if (MySession.Static == null) { ShipyardRunner.ShowMessage("Highlight requires being in a world."); return; }
            IMyCubeGrid grid;
            if (!ShipyardApi.TryGetLookedAtGrid(out grid)) { ShipyardRunner.ShowMessage("Look at the ship you want to compare, then click again."); return; }
            CloseScreen(false);
            ShipyardApi.HighlightCurrent(sel, grid);
        }

        private void OnDelete()
        {
            var sel = SelectedShip();
            if (sel == null) { ShipyardRunner.ShowMessage("Select a ship first."); return; }
            string ownerNote = sel.Owner != null ? " (owner: @" + sel.Owner + ")" : "";
            ShipyardRunner.Confirm("CONFIRM DELETE",
                "Delete '" + sel.CategoryShip + "' from the shipyard" + ownerNote + "?\n" +
                "This removes it from main for everyone. Only the owner can do this.",
                ok => { if (ok) ShipyardApi.DeleteShip(sel); });
        }

        // ---- Review actions ----  (menu stays open; world-viewing actions close it via ActPrInWorld)
        private void ActPr(Action<PrEntry> action, bool needsShip = false)
        {
            var sel = SelectedPr();
            if (sel == null) { ShipyardRunner.ShowMessage("Select a pull request first."); return; }
            if (needsShip && string.IsNullOrEmpty(sel.CategoryShip))
            { ShipyardRunner.ShowMessage("PR #" + sel.Number + " isn't from a ship/... branch.\nReview it on GitHub web instead."); return; }
            action(sel);
        }

        private void ActPrInWorld(Action<PrEntry> action)
        {
            if (MySession.Static == null) { ShipyardRunner.ShowMessage("Visual Diff requires being in a game/world."); return; }
            // World-viewing action: close the menu first so the player is back in-world, then act.
            ActPr(pr => { CloseScreen(false); action(pr); }, needsShip: true);
        }

        private void ConfirmClosePr()
        {
            var sel = SelectedPr();
            if (sel == null) { ShipyardRunner.ShowMessage("Select a pull request first."); return; }
            ShipyardRunner.Confirm("REJECT PR",
                "Reject (close) PR #" + sel.Number + " without merging? The branch will be deleted.",
                ok => { if (ok) ShipyardApi.ClosePr(sel); });
        }
    }
}
