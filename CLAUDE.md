# Hall Pass: notes for Claude

Emby Server plugin that scans music tracks, decides whether each one is explicit (embedded flags, lyrics, online catalogs) and tags it, so a smart playlist can exclude the tag. Design: `docs/DESIGN.md` (written before any code; its Phase 0 says verify the Emby API first, and the findings win over the design). Companion: Tag Team (the bulk tag editor, https://github.com/feerlessleadr/tag-team-emby). Naming and survey decisions: `docs/PLANNING-DECISIONS.md`.

## Read first
- `PROGRESS.md` (what is done, decisions with reasons, open questions: update it whenever something is decided), `docs/API-NOTES.md` (Phase 0 findings, once written).

## Fixed names (never rename later: a rename means a new DLL name, new settings files, new routes)
- Product Hall Pass, short name `HallPass`: DLL `HallPass.dll`, namespace `HallPass`, web addresses `/HallPass/...`, key header `X-HallPass-Api-Key` (if a key is ever needed), settings file `HallPass.xml`, data files `HallPass.*.json`, repo `feerlessleadr/hall-pass-emby`.
- Plugin GUID `6ca80343-2b60-40de-ab81-7a41628fcaf2` (generated 2026-10-12, never changes; Emby identifies the plugin by it).

## Emby plugin facts learned from Hall Monitor (same owner, same machine)
- Package `MediaBrowser.Server.Core` 4.9.1.90, target `net6.0`, tested on Emby Server 4.10.1.0. Reference plugins on this machine: `C:\dev\watching-eye` (Hall Monitor: IService routes, embedded config page, websocket push, regression suite in `tools/`), `C:\dev\githib-repo-update\repo` (GitHub Repo Plugin Install: API versioning, zip installs), the SmartLists plugin.
- A plugin's settings file is named after the DLL (`HallPass.xml` in `programdata/plugins/configurations`). Two copies of one plugin (same GUID, different DLL names) stop Emby loading it.
- A settings page only appears in the dashboard menu if its `PluginPageInfo` has `EnableInMainMenu = true` (set it, with an icon).
- Releases: GitHub Actions builds the DLL, writes a SHA-256, attests provenance, creates the release on a tag that equals `v` + AssemblyVersion. The GitHub Repo Plugin Install plugin installs by the release asset's file name; it supports `.zip` releases that carry extra DLLs but never deletes an old differently named file.

## Test setup
- Page script check: `powershell -ExecutionPolicy Bypass -File tools\Check-PageScript.ps1` after any change to the page script or markup (headless Chrome; a script that does not parse makes the menu item silently do nothing).
- Live alert test: `dotnet build src/HallPass -c Release`, then `powershell -ExecutionPolicy Bypass -File tools\Test-HallPassNotifications.ps1` (backs up and restores the plugin, settings, results and journal by hash, uses two throw-away users, about 15 minutes; `-Force` skips the "someone used the server" check, `-SkipAutoScan` skips the new-track part).
- Test Emby server: `C:\Users\kgiglio\Downloads\embyserver-win-x64-4.10.1.0` (port 8096, media folder `C:\claude`, so `C:\claude\Music` is in its library). It is shared with other sessions: do not restart or stop it while something else may be using it (check sessions first). Real users there (`kevin`, `test1`) are never modified. The admin API key is in `%USERPROFILE%\.emby-spike-key` (never print it). Use throw-away users and clean up.
- Deploy: stop the `EmbyServer` process, copy the DLL into `programdata\plugins`, start `system\EmbyServer.exe -programdata <programdata>` minimized, wait about 25 s. `ilspycmd` and `dotnet` are installed for decompiling and building.
- The music samples are real copyrighted files: never commit them (the `samples/` folder is ignored) and never print lyrics text.

## Conventions
- Commits are authored as `feerlessleadr <github@thecgkg.com>` (never the work email), with `git -c user.name=feerlessleadr -c user.email=github@thecgkg.com commit -F <file>` and the trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Push to main is allowed; tags and releases only when the owner says "publish it".
- Never print, store or commit keys, tokens, passwords or keystores. Ask before behaviour changes; say what was NOT verified.

## Where things are (updated 2026-10-14, version 1.4.0.0)
- **Start with `docs/HANDOFF.md`**: the consolidated state (releases with hashes, the evidence pipeline, decision rules, settings, API, the web page, decisions with reasons, lessons, what is not verified). `PROGRESS.md` is the dated log; `docs/DESIGN.md` is the original design (the code differs where HANDOFF says so); `docs/API-NOTES.md` has the Emby API findings.
- Built and released through 1.3.0.0; **1.4.0.0 (phone alerts for the Embidextrous app) is built and live-tested but not tagged**: the contract as built is `docs/NOTIFICATIONS.md` (samples in `docs/reference/notification-samples.json`, kept in step with the code by `NotificationTests`), the live check is `tools/Test-HallPassNotifications.ps1`. Emby-free alert logic lives in `src/HallPass/Core/Notifications` (event log with a separate `.seq` file, aggregator, catalog); the Emby side is `Services/NotificationHost.cs`, hooked in `ScanJob`, `WriteJob`, `AutoScan`, `ScanTask` and `Plugin.Run` (interrupted-job check). When you change an event, change the catalog, the samples and `docs/NOTIFICATIONS.md` together (a test fails otherwise). Never put a file path in a payload (`EventAggregator.StripPaths`). Not built: Tag Team (separate repo `C:\dev\tag-team-emby`, empty apart from notes).
- Release: `docs/releases/v<version>.md` is the release text and **must be committed before the tag**; the tag `v<AssemblyVersion>` triggers `.github/workflows/release.yml` (tests, build, SHA-256, provenance, GitHub release). Only when the owner says "publish it", and only after CI and the three CodeQL jobs are green on the commit.
- Emby caches a plugin's web page per plugin version: bump `AssemblyVersion` for every change to the page (development versions in between are fine; they are never tagged).
- Owner steps GitHub cannot do from here: CodeQL and immutable releases are on; description and topics set by the owner.
- Copy style for anything the user reads: full plain sentences, American spelling, no sentence ending in "is"/"are", say where a setting lives.
- Verify, do not guess: reproduce a reported problem with the real files and print only matched words and positions, never lyrics (see HANDOFF section 7).
