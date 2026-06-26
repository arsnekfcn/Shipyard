# Shipyard
**NOTICE OF PROPRIETARY SYSTEMS.**
You are accessing SHIPYARD™, a sanctioned hull-provenance and revision-control apparatus, 
brought to you by the Formidan Mandate.

## What is shipyard
A Space Engineers plugin, loaded via **Pulsar**, that puts a Git-backed
blueprint workflow inside the game. Browse a shared repo of ships, install / paste /
project them, publish your own blueprints, review and merge changes, push to the Steam
Workshop (unlisted, wiped GPS), and see a **visual in-world diff** of what changed between versions
without leaving SE. Works **online** (GitHub-backed, multi-user) or **offline**
(local git, you host your own remote if you want to).

It is skinned as a **Formidan Mandate** proprietary corp terminal. 
The skin is cosmetic; the tool is "Shipyard".

---

## What it does (current feature set)

### Online mode (GitHub-backed, multi-user)
- **In-game GitHub sign-in:** OAuth **device flow** via Octokit (same UX as `gh` CLI:
  opens browser, shows a one-time code). Token is **DPAPI-encrypted** per Windows user (see SECURITY.md).
- **Multi-user:** every user authenticates to *their own* GitHub account and points at
  a repo owned by themselves or a friend (Owner / Repo / top-folder). Previously-used repos are
  remembered for one-click switching ("Saved" dropdown).
- **Publish via the GitHub Git Data API directly** (no git/PowerShell on the client):
  scrub → create blobs/tree/commit → branch → PR. Independent calls fire concurrently.
- **Checkout workflow:** *Check Out* (lock + paste the ship into the world), build, *Commit
  looked-at ship* (one or many commits to your work branch), *Open update PR*, then *Release
  checkout* (clears the lock). First commit of a session reads the base version; the rest
  diff in-memory.
- **Review:** open PRs: details, visual diff (spawn-in-world), install-this-version,
  approve, request-changes, merge, reject.
- **Access management** (admin): add / set / remove collaborators (read-only / write / admin),
  view + accept invitations. Maps to GitHub collaborator perms.
- **Ownership + merge gating** via `.github/CODEOWNERS`: a ship's owner must approve before
  someone else's PR to it can be merged (enforced in-plugin).
- **Create or init a repo in-game:** after sign-in, a new user can have the plugin **create a
  private Shipyard repo** on their account — fully seeded (`.gitattributes`, `.gitignore`,
  `CODEOWNERS`, top folder, README) and set active — with one click, never touching GitHub's website.
  "Initialize repo" seeds an *existing* repo the same way. *(Creating a repo needs device-flow sign-in /
  the broad `repo` scope; a fine-grained BYO app can't create repos — point it at an existing one.)*

### Offline mode (local git, no account/network)
- **First-boot chooser:** pick **Online** or **Offline** the first time; switchable later in
  Settings. Offline uses a **local git repo** (embedded LibGit2Sharp) at a folder you pick.
- **You own the remote:** if you even want one. The plugin never creates or manages a remote; 
  point it at a folder that is itself a git repo (or sync it to your own host however you like). Saves and
  updates **commit straight to local `main`** (no local branching yet).
- **Full local workflow:** save new ships, update existing ones (commit the looked-at grid),
  install / paste / project, visual diff, and delete without signing in.

### Cross-cutting
- **Steam Workshop:** push a repo ship to the Workshop using the game's own
  publish pipeline. **Unlisted by default** (link-only); the published item id is remembered
  **per user**, so pushing again **updates the same item in place** instead of creating a
  duplicate. On success it opens the item's page in the **in-game Steam overlay**.
- **Privacy scrub on every upload / commit:** grid rebased to origin; owner / BuiltBy /
  SteamID / OwnerSteamId / WorkshopId zeroed; remote-control + **AI-block** (autopilot / recorder /
  defensive-combat) waypoints & coordinates cleared; **GPS coordinates scrubbed** inside CustomData /
  mod storage / **LCD & text-surface panels**. Best effort; **programmable-block scripts are NOT touched**.
- **Visual diff** (`HighlightManager`): non-destructive wireframe boxes on changed blocks.
  **Defaults: GREEN added, RED removed, ORANGE replaced (type swap), CYAN repainted, MAGENTA
  custom-data changed.** Plus a floating label on whatever changed block you look at. Aim at
  a MAGENTA box and press **Ctrl+Shift+D** for a line-by-line CustomData diff. Two modes:
  spawn-the-PR-version, or highlight-on-a-grid-already-in-world. Compares the **primary hull
  grid** (subgrids excluded) and recovers a shifted pivot so a re-origin doesn't read as
  "everything changed".
- **Configurable highlights** (Settings screen): max box count, distance cap, per-category
  show/hide, **custom hex colors** (colorblind-friendly), and **xray** (draw boxes through
  walls as a wireframe).
- **Chat terminal** (`ChatCommands`): `/sy` or `/shipyard` + `ls/cd/pwd/folders/ships/find`
  (terminal-style nav over the repo tree) and
  `install/pull/spawn/project/diff/clear/browse/review/commit/xray/show/hide/filters/help`.
- **Hotkey** to open the Shipyard: default **Ctrl+Shift+S** configurable in
  `%APPDATA%\Shipyard\hotkey.txt`, plus a "Shipyard" button injected into the F10 blueprint screen.

> **Diff scope note:** the data diff tracks **CustomData only**.

---

## Architecture / file map

Single C# project (`net48`), namespace `ShipyardPlugin`, output **`Shipyard.dll`**
(so Pulsar lists it as "Shipyard"). **Octokit and LibGit2Sharp are NuGet `PackageReference`s** —
Pulsar's marketplace build compiles from source and restores them from NuGet (no binary DLLs are
committed to the repo). The local `deploy.sh` build ships them — and LibGit2Sharp's native
`git2-*.dll` — as **separate files** alongside `Shipyard.dll` in `Legacy\Local\Shipyard\`
(`Plugin.InitLocalGit` points LibGit2Sharp at the native; the logo loads from the plugin asset folder).

Source is grouped into **`Core/`** (logic), **`Screens/`** (in-game GUI), **`Patches/`** (Harmony),
and **`Tools/`** (build helpers); `Plugin.cs` (entry point), the `.csproj`, `Shipyard.xml`, `assets/`,
and `deploy.sh` sit at the root.

| File | Responsibility |
|---|---|
| `Plugin.cs` | `IPlugin` entry point. Harmony patch-all; `LoadAssets` receives Pulsar's asset folder; `InitLocalGit` points LibGit2Sharp at the native git2; `Update()` drives chat commands, hotkey, Ctrl+Shift+D, and `HighlightManager.Draw`. |
| `Core/ShipyardApi.cs` | **Core (online).** Octokit calls: sign-in, repo save/init, collaborators/invites, fetch ships+PRs, publish, checkout/commit/PR/release, install/spawn/project, visual diff, compare/merge/approve/close/delete, privacy scrub. `ShipEntry`/`PrEntry`/`ManageData` models. |
| `Core/ShipyardApi.Offline.cs` | **Core (offline).** `partial class ShipyardApi`: local-git backend (init/read/save/commit/install/publish/delete) via LibGit2Sharp. |
| `Core/Auth.cs` | Per-user config (`%APPDATA%\Shipyard\config.json`) + DPAPI token (`token.dat`). Online repo + client id, mode/local-repo/author, and all highlight/diff prefs. |
| `Core/WorkshopPush.cs` | Steam Workshop publish/update via the game's own pipeline; per-user item-id map (`workshop.json`); opens the page in the Steam overlay. |
| `Core/HighlightManager.cs` | In-world wireframe diff boxes (depth-tested or xray), look-at labels, visible-subset budgeting. |
| `Core/ShipyardRunner.cs` | Thread/GUI plumbing: `InvokeOnMain`, busy/sign-in overlays, background workers, notifications. |
| `Core/ShipyardErrors.cs` | Friendly error mapping (e.g. wipe a bad token on `AuthorizationException`). |
| `Core/ChatCommands.cs` | `/sy` chat command parser + terminal navigation. |
| `Screens/ShipyardScreen.cs` | The tabbed Browse / Publish / Review UI + per-frame animation (offline hides Review). |
| `Screens/SettingsScreen.cs` | Account + repo settings (the "Account" screen; opens when nothing is configured). Sign-in/out, repo + saved-repo picker, BYO-app entry, "work offline". |
| `Screens/HighlightOptionsScreen.cs` | The **Settings** screen for diff highlights: count, distance, per-category show/hide, hex colors, xray, clear-all. |
| `Screens/ModeChooseScreen.cs` | First-boot Online / Offline chooser. |
| `Screens/OfflineSetupScreen.cs` | Offline setup (local folder / top folder / author; switch back to Online). |
| `Screens/ByoAppScreen.cs` | "Use your own GitHub App" (Option 2) — enter your public Client ID only. |
| `Screens/ManageAccessScreen.cs` | Admin collaborator management. |
| `Screens/PublishNewScreen.cs` | Step-2 form for "publish as NEW ship" (name / folder / tags). |
| `Screens/{ConnectRepo,CreateRepo}Screen.cs` | Connect to / create-and-seed a Shipyard repo in-game. |
| `Screens/TextDiffScreen.cs` | Line-by-line (LCS) CustomData diff window (Ctrl+Shift+D); collapses unchanged runs. |
| `Screens/DialogScreen.cs` | In-style result/confirm dialog (replaces the stock message box). |
| `Screens/TerminalScreen.cs` | Animated busy / sign-in overlay. |
| `Screens/BootScreen.cs` | 80s-style boot sequence shown while the repo fetches on open. |
| `Screens/InfoScreen.cs` | Scrollable multi-line text view (help, PR details). |
| `Screens/Frame.cs` | Shared chrome: divider, classification footer, button/label constructors. |
| `Screens/Brand.cs` | Lore skin: faction strings, slogans, amber palette, logo path. |
| `Patches/BlueprintScreenPatch.cs` | Harmony postfix that injects the "Shipyard" button into F10. |
| `Tools/Publicize.cs` | Supplies the `IgnoresAccessChecksTo` trigger so Pulsar's from-source build publicizes `Sandbox.Game`. |
| `assets/logo.png` | Mandate crest (shown on the auth screen). Shipped as a plugin asset (`<AssetFolder>`), loaded via `Plugin.LoadAssets`. |

---

## Build & deploy

Requires `dotnet.exe` and the SE game DLLs (path via the `SeBin64` msbuild prop in the
`.csproj`; defaults to a Steam install path. Override if yours differs).

```bash
# build Release + copy into Pulsar's Legacy\Local\Shipyard\
bash deploy.sh
```

`deploy.sh` does: build Release → copy `Shipyard.dll` into `<Pulsar>\Legacy\Local\Shipyard\`.
If the DLL is locked (game
running) it renames the old one aside (`.dll.old`, inert — Pulsar only scans `*.dll`) and
copies fresh. The logo + native libgit2 self-extract at runtime, so nothing else is copied.

**Pulsar** scans `Legacy\Local\` recursively for `*.dll` at startup; the plugin Id is the DLL
filename. After deploying, **restart SE via Pulsar** and enable "Shipyard" under Local.

> ⚠️ If SE is running it holds a lock on the DLL. `deploy.sh` handles this with the rename-aside
> fallback, but closing the game first is cleanest.

---

## Configuration (per user, at `%APPDATA%\Shipyard\`)

- `config.json` — hand-rolled JSON. Keys: `clientId` (blank = built-in app), `repoOwner`,
  `repoName`, `rootFolder` (user-set, default `Fleet`), `login`, `mode` (`online`/`offline`), `localPath`,
  `localAuthor`, `repos` (saved shipyards), `xray`, `diffHidden`, `highlightCount`,
  `highlightDist`, `diffColors`, `bgColor`, `bgAlpha`, `hidePopups`.
- `token.dat` — DPAPI-encrypted GitHub token.
- `workshop.json` — per-user map of repo ship → published Workshop item id.
- `hotkey.txt` — open hotkey; first non-`#` line, e.g. `Ctrl+Shift+S`. (B is rejected — SE blueprint key.)
- `native\git2-*.dll` — self-extracted native libgit2 (offline-mode engine).
- Built-in OAuth app client id: public; device flow needs no secret.

---

## Linux

Pulsar has Linux-capable variants, so Shipyard should run on Linux largely unchanged. **This probably
works but has not been fully tested by the author** — please report any issues. Notes:

- **Offline mode is the zero-friction path.** Local git, no account, no browser, no token.
- **Online sign-in** works via device flow; if the browser doesn't auto-open, use the URL + one-time
  code shown on the sign-in screen (open `github.com/login/device` yourself).
- **Token storage** uses Windows DPAPI; where that isn't available it won't crash — you stay signed in
  for the session and re-auth next launch.

---

## Security & trust

See **[SECURITY.md](SECURITY.md)** for the full model, per-option setup, and known limitations.

---

## Roadmap / backlog (post-1.0)

- **Block-settings diff spike:** Track vanilla terminal settings + mod storage (WeaponCore)
  deterministically (currently CustomData-only; the rest is non-deterministic noise).
- **Local branching** for offline mode (1.0 commits straight to local `main`).
- **Multi-grid / subgrid diff** (currently primary hull grid only).
- Tags + tag search; clipboard-as-update-source; real F10 tile badge; resolution sweep for UI layout.

---