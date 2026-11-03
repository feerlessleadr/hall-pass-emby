# Hall Pass: status

Last updated: 2026-11-03 (docs setup session). Living snapshot; overwrite it. The consolidated technical picture is `docs/HANDOFF.md`; the dated log is `PROGRESS.md`.

## State
- Released: **1.4.0.0** on 2026-10-14 (phone alerts for the Embidextrous app). HEAD 97cc2af "Record the release". `main` is in sync with `origin/main`; working tree clean (verified 2026-11-03).
- Earlier releases: 1.0.0.0 to 1.3.0.0 (see `docs/releases/` and the table in `docs/HANDOFF.md` section 1).
- Plugin: net6.0, `MediaBrowser.Server.Core` 4.9.1.90, tested only on Emby Server 4.10.1.0. 337 unit tests (per HANDOFF; the `Core/` folder is Emby-free and compiled into the tests). Live alert test passed 52/52 on the released DLL.
- What it does: scans music tracks, decides explicit, clean, likely-clean, needs-review or skipped from evidence, and writes exactly one of four tags (`explicit`, `clean`, `likely-clean`, `explicit-review`). Dry run is the default. Nightly scan at 3:30; new tracks are auto-scanned.

## In progress
Nothing. No open feature work.

## Next steps
- Merge or close three Dependabot branches: `Microsoft.NET.Test.Sdk` 18.10.1, `xunit` 2.9.3, `xunit.runner.visualstudio` 4.0.1. Owner approval needed; review release notes first.
- Trial on the owner's real production server (probably Windows, run as the Emby tray app; see WORKSPACE.md).
- Ideas not built: second lyrics source, genre rules, CSV export, shared tag-writer code with Tag Team.

## Connections
- Embidextrous app talks to `/emby/HallPass/...` with an administrator token (min plugin 1.2.0.0; events need 1.4.0.0). Phone alerts use the shared push contract (`HallPassEventsStart` / `HallPassEvents`). This repo documents its side in `docs/NOTIFICATIONS.md`; the shared shape is in Embidextrous `docs/push-contract.md`.
- Tags it writes feed SmartLists playlists as a rule field. It is a data link through Emby tags only.
- This repo is the template for Label Maker and Tag Team.

## Known issues and not verified
- Not verified: Linux or Docker servers, libraries over a few hundred tracks, other Emby versions, NFO savers turned on, Emby stopping mid-write, real Failed states, the nightly summary, phone widths under 600 px, some lowercase word cases.
- Alerts delivered through `SendMessageToAdminSessions` reach only dashboard sessions, not the app; the app uses the websocket subscription instead.
- The Embidextrous Hall Pass tab exists, but nothing was verified on a real phone.

## Manual items for the owner
- Say "publish it" before any tag or release.
