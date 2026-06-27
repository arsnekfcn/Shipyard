# Security & trust

Shipyard is open source. Read the code, and verify these claims for yourself. This document explains
where your credentials live, what the plugin can and can't do, and the answer to the question that
matters most: **can anyone else reach your repositories through this plugin? No.**

## TL;DR

- Your GitHub token is created **on your machine**, stored **encrypted** (Windows DPAPI), and goes
  **only to GitHub's own API**. There is **no Shipyard backend and no code path that sends your token
  anywhere else.** Not to the author, not to anyone. That's a property of the open source you can read
  (see *[Can the plugin author access your repos?](#can-the-plugin-author-access-your-repos-no)*).
- In online mode the plugin only talks to **`api.github.com`** (plus **Steam's own** publish pipeline if
  you use Workshop), and only acts on the **one repo you configure**.
- The **plugin author cannot access your repos.** This is by design and is built into the authentication system.
- You choose your trust level: **Option 1** (device-flow sign-in, simplest), **Option 2** (your own
  fine-grained GitHub App, least privilege), or **Option 3** (fully **local git.** No account, no
  network: keep it offline forever, or sync to a remote *you* build and control).

## How sign-in works

Sign-in uses GitHub's **device flow.** This is the same one-time-code browser flow the official `gh` CLI uses.
You click *Sign in*, a browser opens to `github.com/login/device`, you enter the shown code and
authorize. GitHub hands the resulting **user token directly to your game client**; it's written to
`%APPDATA%\Shipyard\token.dat`, **encrypted with your Windows account (DPAPI)** so only you, on that PC,
can read it. The token is *yours*. The plugin's **only** outbound network calls are to GitHub's own API
(`api.github.com`) — there is no mechanism that could route your token anywhere else (see
[Can the plugin author access your repos?](#can-the-plugin-author-access-your-repos-no) below).

---

## Two ways to run it online by default. Pick your trust level

### Option 1: Device flow (high-to-moderate trust)

**Best for:** most people without major concerns about repo safety.

- Sign in with the **built-in public Client ID** (a device-flow client ID is *public by design* not a
  secret), or register **your own** OAuth App, enable Device Flow, and paste its Client ID in
  *Account → Settings* so the shared app isn't involved at all.
- Your token stays on your machine (encrypted) and is sent **only to GitHub**. There is no code path
  that could deliver it anywhere else. In short:
  **the author cannot act as you or reach your repos.**
- **Trust basis:** the plugin is open source and reviewed. You can confirm it only contacts GitHub and
  only touches the repo you set. The Pulsar marketplace reviewers also can confirm this.
- **Honest caveat (scope):** a classic OAuth App can only request the broad **`repo`** scope for private
  repos. That's a GitHub limitation (the same scope `gh` and most git tools request), not a Shipyard
  choice. So *your own* token is technically capable of reaching *your other* private repos. The author
  still can't, but if *your* machine or token were compromised, the blast radius is wider than one repo.
  If that bothers you, use Option 2.

### Option 2: Your own GitHub App (low trust / least privilege)

**Best for:** security-conscious owners who want a reduced blast radius.

Your own **GitHub App** with fine-grained permissions. **Contents** (read/write blueprints) and
**Pull requests** (publish/review). Installed on **just your shipyard repo**. The token can touch
**only that one repo**, nothing else you own, and **the plugin author owns nothing and holds no key.**

**Setup (in-game):** Account → **"Use your own GitHub App":** a step-by-step manual. The plugin only
opens GitHub's create-app page; **you click every button yourself** (deliberately hands-off. The plugin
author assumes anyone choosing this option does not want said author pushing buttons automatically):

1. **Open GitHub: create an app**, then on that page set: a unique **name**; any **homepage URL**;
   uncheck the **Webhook** "Active" box; **Contents: Read and write**; **Pull requests: Read and write**;
   check **Enable Device Flow**; "Where can this be installed?" → **Any account** (so crew can sign in).
   Click **Create GitHub App**.
2. On the new app's page: **uncheck "Expire user authorization tokens"** (Save), then **Install App** on
   your shipyard repo.
3. Copy the app's **Client ID** (its *General* page) into the plugin and **Save & Sign in**.

- **Why uncheck "Expire user authorization tokens"?** With expiry on, refreshing a token needs the client
  *secret*; with it off, the public Client ID alone carries the device-flow sign-in — **no secret, ever.**
- **The Client ID is public:** safe to hand to your crew so they sign into the same app. This is not required,
  your crew can choose to use the same app to access the repo but if they are granted access, they can use any
  device flow authorization method.
- **Onboarding crew:** the app installed on the repo **and** each builder being a collaborator is what
  grants access. Add collaborators (on github.com, or *Manage Access* if your app also has Administration);
  they device-flow sign in with your Client ID. Their token = *their* access ∩ the app's permissions —
  least privilege, and they can't reach anything they couldn't already.

### Option 3: Fully local git (zero trust / no account, no network)

**Best for:** anyone who wants to use *none* of the above. No GitHub, no sign-in, no token, and no
network calls of any kind.

Pick **Offline** at the first-run chooser (or switch to it later in *Settings*). Shipyard then keeps your
fleet in a **local git repository** on a folder you choose, using an embedded git engine
(LibGit2Sharp). Saves and updates are ordinary local commits. There is **no token, no account, and the
plugin makes no network calls at all in this mode.** There is nothing to send anywhere because nothing
is created or transmitted.

- **Keep it offline forever.** A local repo on your disk is a complete, valid git history. Back it up like
  any folder. Nothing ever leaves your machine.
- **Or sync it to a remote *you* control:** entirely with *your own* tools, outside the plugin. Because
  the local folder is a normal git repo, you can point it at **any** remote you like: a private GitHub
  repo under your own account, a self-hosted Gitea/GitLab, a server on your LAN, a thumb drive... whatever
  cloud (or no-cloud) solution you want. **The plugin never creates, configures, authenticates to, or
  pushes to a remote.** It only reads/writes the local repo; you run `git push`/`git pull` (or your host's
  sync) yourself. Your hosting is yours alone. The author has no part in it and no visibility into it.
- **Trust basis:** there is no trust to extend. With no token, no account, and no network path in this
  mode, there is nothing the plugin *or* the author could do with credentials you never created.

---

## Shrinking the device-flow blast radius (Authentication Option 1)

Option 1's token carries the broad `repo` scope, GitHub's only scope for private repos, so a
*compromised build* of the plugin could touch any of your private repos, not just the shipyard. The author
still can't reach them; this is purely about limiting what a subverted plugin on **your own machine** could
do. Two ways to shrink it:

- **Use a dedicated GitHub account.** Make a free, separate account used *only* for the shipyard, owning
  *only* that one repo. The broad token then has nothing else to reach. Its blast radius is a single repo
  on a throwaway account. Simple and effective.
- **Self-host an app (Authentication Option 2).** A fine-grained GitHub App scoped to the one repo limits the token to
  that repo and only the permissions you grant, on your real account.
- **Beyond those, there's no further mitigation we're aware of.** A classic OAuth token for *private*
  repos is broad by GitHub's design. You can narrow what the *account* owns (dedicated account) or narrow
  what the *token* can do (your own app), but there's no setting that makes the shared-OAuth token itself
  narrower.

## "Can the plugin author access your repos?" No.

To re-iterate: This is not due to policy or my good intentions. 
**The code contains no mechanism to do it, and the code is open for you (or anyone) to audit.**
The plugin's entire network surface is calls to GitHub's own
API; there is **no author-operated server, endpoint, telemetry, or phone-home of any kind** anywhere in
the source. A token can only travel where the code sends it, and the code sends it only to `api.github.com`.
So this is verifiable by construction, not "I pinky promise not to look."

**Option 1 (shared OAuth app):** an OAuth app has **no installation tokens and no private key.** There is
no mechanism by which the author could mint a token for your account. Your token is created by device flow
directly on your machine and is sent **only to GitHub**; the client secret the author holds **cannot**
produce a user token without you authorizing it. The author has **zero access path.**

**Option 2 (your own GitHub App):** you hold your app's key; the author holds nothing of yours. Access is
impossible by construction.

**Option 3 (local git):** there is no token, no account, and no network call at all. There is nothing in
existence for anyone, author included, to access. If you sync to a remote, *you* own that remote and the
plugin isn't involved in it.

**What Shipyard deliberately does NOT do: Offer a *shared GitHub App that the author hosts*.** A GitHub
App has a private key, and whoever holds it can mint **installation tokens** for every repo the app is
installed on. i.e. the author could pull them all (within whatever permissions each install granted).
That capability is latent the moment the key exists, used or not. We refuse that model for exactly this
reason, and you should be wary of any tool that asks you to install *its own* GitHub App on your repo.
This is offered as an option for self-hosting because, presumably, a repo owner is not concerned with
minting keys to seize access to a repo they own.

## What permissions, and why

- **Option 1 (OAuth App):** `repo`: GitHub's only scope that covers *private* repos. Broad by GitHub's design.
- **Option 2 (GitHub App):** **Contents R/W** (store/fetch blueprints), **Pull requests R/W** (publish &
  review), **Administration R/W** (only for in-game collaborator management). Scoped to the repo(s) you select.
- **Option 3 (local git):** **none**: no account, no token, no permissions, no network.

> The in-game **"Create a new Shipyard repo"** convenience uses the `repo` scope's repo-creation
> capability, so it works on **Option 1** only. A fine-grained **Option 2** app can't create repos —
> make the repo yourself (or with Option 1) and point the app at it.

## Blueprint privacy: what gets scrubbed before upload

A blueprint captured from the world embeds **where it was built** and **who built it**. Before
*anything* is pushed to your repo (both publishing a new ship and committing WIP), Shipyard scrubs:

- **World position**: every grid's stored position/orientation is rebased so the primary grid sits at
  the origin (sub-grids keep their relative offsets, so paste/spawn are unaffected). The blueprint stops
  recording the spot it was saved.
- **Autopilot / AI waypoints**: remote-control waypoints + coordinates, and AI-block mission data —
  Automaton (autopilot route, recorded "home"), AI Flight/Move + Recorder autopilot paths, and the
  Defensive Combat block's flee / last-known-enemy coordinates — are cleared. These are raw world positions.
- **GPS in CustomData / mod storage / LCD text**: CustomData and mod storage are *kept* (so the plugin can
  diff loadout changes), and LCD / text-surface panels keep their text — but any `GPS:name:x:y:z:` token has
  its **coordinates zeroed** (`GPS:name:0:0:0:`). The entry stays so scripts still parse it; the location is gone.
- **Ownership** — block `Owner`/`BuiltBy`, the blueprint's `OwnerSteamId`, and any SteamID64 are zeroed;
  per-user Workshop ids stripped.
- **Nested projector blueprints** are scrubbed the same way, recursively.

**Honest limits.** The GPS scrub targets the standard `GPS:` token format, so coordinates in a bespoke
encoding could slip through. **Programmable-block scripts (their source and `Storage`) are deliberately NOT
touched** — breaking someone's navigation script would be worse than the rare leak, so review/remove
sensitive GPS in your own scripts. CustomData and LCD text are shared with everyone who can read the repo;
if you keep genuinely sensitive data in a block, review it (the in-game Data Diff shows exactly what's
stored) or use a dedicated account for the shipyard.

**Reasoning, and future plans:** I do not intend to make this feature optional, though if there is demand
I will consider it further. I do NOT like that Keen embeds all of this data. Many new players who don't
know better have been burned by some gremlin living on new listings on the workshop to steal their base
coordinates. If someone has a legitimate use-case where they would want blueprints pushed to their repo
to retain this data, I will certainly consider adding that as an option. I will not support pushes to 
Steam Workshop retaining this data, access control is poor and the method is generally "security through
obscurity."

## Where your data lives

- `…\AppData\Roaming\Shipyard\token.dat` — your GitHub token, **DPAPI-encrypted** (current Windows user,
  that PC). *Online mode only — not created offline.*
- `…\AppData\Roaming\Shipyard\config.json` — mode (online/offline), repo owner/name, top folder, Client ID,
  and (offline) your local repo path + author name. **No secrets.**
- **Offline mode:** your fleet lives in the **local git repo folder you chose** — an ordinary git
  repository on your disk.
- **Network:** online mode calls **`api.github.com`** (and **Steam's own** publish pipeline if you use
  Workshop). **Offline mode makes no network calls at all.**

## Known limitations

- **Ownership enforcement** is applied *in the plugin* (it reads `.github/CODEOWNERS`, and the in-game
  Merge button requires the owner's approval). Someone merging via the GitHub *website* can bypass it
  unless the repo has native "require review from Code Owners" (needs GitHub Pro on a personal repo, or
  Team on an org). On a free repo, trust the plugin's enforcement.
- **One token per Windows user** (DPAPI). Separate Windows users on one PC each sign in separately.
- **Requesting access** means giving your GitHub username to the repo owner. GitHub has no private-repo
  "request access" API. The owner adds you; you accept the invite in-game.
- **Concurrent edits** to the same ship can conflict; the plugin refuses non-clean merges. There is a  
  rebase feature, but if you have truly gummed something, resolve those on the GitHub website. I will
  make a best-effort to resolve any in-game behavior that led to a bad repo-state. I can't do anything to
  help with changes you made outside of the plugin.
- **Mods/DLC blocks:** a ship using content you don't have installed shows placeholder/ghost blocks.
- **Linux (Proton):** SE runs under Wine, so the plugin uses Windows DPAPI for the token; it persists
  within your Proton prefix. If DPAPI is unavailable it degrades to a **session-only** token. Never
  plaintext, never a crash. Offline mode (Option 3) needs no token at all and is the friction-free Linux path.
  I consider Linux support to be "Beta" currently, and has undergone essentially zero testing. Please report issues.

## Reporting a problem

Found a security issue? Please contact the maintainer privately rather than opening a public issue, so it
can be fixed before disclosure.
