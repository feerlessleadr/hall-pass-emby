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
- Test Emby server: `C:\Users\kgiglio\Downloads\embyserver-win-x64-4.10.1.0` (port 8096, media folder `C:\claude`, so `C:\claude\Music` is in its library). It is shared with other sessions: do not restart or stop it while something else may be using it (check sessions first). Real users there (`kevin`, `test1`) are never modified. The admin API key is in `%USERPROFILE%\.emby-spike-key` (never print it). Use throw-away users and clean up.
- Deploy: stop the `EmbyServer` process, copy the DLL into `programdata\plugins`, start `system\EmbyServer.exe -programdata <programdata>` minimized, wait about 25 s. `ilspycmd` and `dotnet` are installed for decompiling and building.
- The music samples are real copyrighted files: never commit them (the `samples/` folder is ignored) and never print lyrics text.

## Conventions
- Commits are authored as `feerlessleadr <github@thecgkg.com>` (never the work email), with `git -c user.name=feerlessleadr -c user.email=github@thecgkg.com commit -F <file>` and the trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Push to main is allowed; tags and releases only when the owner says "publish it".
- Never print, store or commit keys, tokens, passwords or keystores. Ask before behaviour changes; say what was NOT verified.
