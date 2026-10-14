# Hall Pass phone alerts: the contract as built

**Status: built in 1.4.0.0.** This file describes what the plugin really sends. It follows the shared push contract of the Embidextrous app (`docs/push-contract.md` in that repo), which Label Maker implemented first (`docs/NOTIFICATIONS.md` in the Label Maker repo). Sample payloads are in `docs/reference/notification-samples.json`; a unit test fails when those samples and the real catalog or event fields drift apart.

## 1. How it works
1. **Subscription (how the app receives alerts).** The app connects to Emby's websocket with its token in the `X-Emby-Token` header, so Emby does not link that socket to a user session and `SendMessageToAdminSessions` never reaches it. The app therefore subscribes, the way Hall Monitor's live status works: it sends `{"MessageType":"HallPassEventsStart","Data":"0,30000,<afterSeq>"}` and the plugin pushes to that one socket. Details in section 5b.
2. **Push to Emby's own admin sessions.** When something worth an alert happens, the plugin also calls Emby's `ISessionManager.SendMessageToAdminSessions(name, data, cancellation)`. The message arrives as `{"MessageType":"HallPass.ScanFinished","Data":{...}}` on the websocket of every connected **administrator** session. A non-administrator session and a socket opened with only an API key get nothing. It is fire and forget: the sender logs a failure and swallows it, so an alert can never break a scan or a write job. Only sockets that carry the token in the address (`?api_key=`), such as Emby's own web dashboard, receive this; the app does not rely on it.
3. **Catch-up.** Every event gets a number `Seq` and is kept in a small log (the last 500 events). `GET /emby/HallPass/Events?After=<seq>&Limit=100` returns what an app missed. The app dedupes by `Seq`.
4. **Catalog.** `GET /emby/HallPass/Notifications/Catalog` describes every event (title, description, parameters, suggested rule, lock-screen text) so the app can build its rule screen from the server.

All routes need an administrator token in `X-Emby-Token`. There is no separate key.

## 2. Version gate
`GET /emby/HallPass/Info` now returns `{ Name, Version, ApiVersion: 1, ApiLevel: 1, Features: ["events","catalog"] }`.
- `ApiVersion` is the plugin's own API version and **stays 1** (the app's existing Hall Pass tab gates on it and on `Version`, so nothing there changes).
- `ApiLevel` is the level of this notification contract (the same name the shared contract uses). Every pushed event carries it. It grows only when a field is removed or changes meaning.
- `Features` lists `events` and `catalog`. The older plugin 1.3.0.0 and earlier has neither, so an app should gate the Hall Pass alerts on `Features` containing `events`.

## 3. The events
Every `Data` object starts with the **common fields**: `ApiLevel` (int), `Seq` (long, grows by 1 per event, saved so it survives restarts and never goes back), `EventId` (GUID), `ServerTimeUtc` (ISO 8601, `...Z`), and `Event` (the short name). The message name is `HallPass.` plus the short name. Numbers are JSON numbers.

| Message | Sent when | Event fields | App rule parameters |
|---|---|---|---|
| `HallPass.ScanFinished` | A scan ended Completed. A scan that you started (Scan now, Rescan everything, or the app) always reports, even when nothing changed. The nightly scheduled scan and the automatic scan of new tracks report only when they looked at **25 or more** new or changed tracks. | `Total`, `Scanned`, `Unchanged`, `Failed`, `NeedsReview` (tracks waiting for a person now), `OnlineFailed` | none (default on) |
| `HallPass.ScanFailed` | A scan ended Failed or Cancelled (whoever started it). | `State` (`Failed` or `Cancelled`), `Message` (plain sentence) | none (default on) |
| `HallPass.ApplyFinished` | A write job ended Completed: Apply or Undo. Writing that happens by itself (after the nightly scan or after the scan of new tracks) reports only when it changed **25 or more** tracks. | `Kind` (`Apply` or `Undo`), `Changed`, `Unchanged`, `Failed`, `Conflicts` | none (default on) |
| `HallPass.ApplyFailed` | A write job ended Failed or Cancelled (always, even when automatic), **or** the plugin found an unfinished job in the journal after a restart (the server stopped in the middle of it). | `Kind`, `State` (`Failed`, `Cancelled` or `Interrupted`), `Message` | none (default on) |
| `HallPass.ReviewQueue` | The number of tracks waiting for a person went **up** since the last look, and at least 10 minutes passed since the last such alert. | `Count` (waiting now), `New` (how many more than at the last look) | `MinCount` int 1 to 100000, default 50 (default **off**) |

Details that the table cannot say:
- **Quiet automatic scans send nothing.** The automatic scan of new tracks works in batches of up to 50. The plugin adds up the batches and sends **one** summary when its queue is empty, and only if the total was 25 or more tracks. A bulk import of 1,000 tracks makes one `ScanFinished`, not 20.
- **"Waiting for a person"** means tracks whose decision is Review (the Review tab's "Needs review" queue).
- **ReviewQueue** is checked when a scan finishes and when the automatic scan summarizes. A rise held back by the 10 minute spacing is announced by the next look. A drop, a manual decision, a Rescan this track, and saving the settings only lower or reset the remembered number silently, so the owner's own actions never alert. Switching the alert on later does not announce an old rise. At startup the current number is remembered without an alert.
- **Interrupted jobs.** A journal file with no end line, found when the plugin starts, makes one `ApplyFailed` with `State: "Interrupted"` (several at once make one event about the newest, and the sentence says how many older ones were found). Each unfinished job is announced once; the memory is saved, so another restart does not repeat it.
- **Kind** is `Apply` for an apply, for the automatic write after a scan (the journal calls it `auto`) and for the nightly apply; `Undo` for an undo.
- **No paths.** Payloads describe things by counts and plain sentences only. Every sentence copied from an error has drive paths (`C:\...`), network paths (`\\server\...`) and Unix paths (`/mnt/...`) replaced by "(a folder)" and is cut at 300 characters. No track, artist, album or lyrics text is ever in a payload.

## 4. The catalog route
`GET /emby/HallPass/Notifications/Catalog` returns `{ ApiLevel, Service: { Id: "HallPass", Title: "Hall Pass", ChannelId: "hall-pass", ChannelTitle: "Explicit music (Hall Pass)" }, Events: [ { Id, Title, Description, Params, DefaultRule: { Enabled, Params }, LockScreenText, OpensTab } ] }` with five entries. `LockScreenText` is the generic text for the lock screen (no names). `OpensTab` is a hint where a tap could lead: `HallPass/Dashboard` (scan events), `HallPass/History` (write events) and `HallPass/Review` (review queue); the app may ignore it. The full catalog is in `docs/reference/notification-samples.json` under `catalog`. The channel id and title match the ones the app already has for Hall Pass.

## 5. The catch-up route
`GET /emby/HallPass/Events?After=<seq>&Limit=100` returns `{ LatestSeq, Truncated, Events: [ { Seq, Name, AtUtc, Data } ] }`, oldest first, from a log of the last 500 events. `Limit` defaults to 100 and is never more than 500; `After` defaults to 0. `Truncated: true` means the log no longer reaches back that far (the app should then read `GET /emby/HallPass/Status` instead). The log is in `HallPass.Events.json` in the plugin's configuration folder, and the highest number is also kept in `HallPass.Events.json.seq`, so numbers never go back, even if the log file is damaged (a damaged file is set aside as `.corrupt`). The aggregator's memory (the remembered review queue size, announced interrupted jobs) is in `HallPass.Notifications.json`.

## 5b. The events subscription (websocket)
The app's way to receive alerts. Connect to `ws://<server>/embywebsocket?deviceId=<id>` with the administrator token in the `X-Emby-Token` header (no `api_key` in the address), then:
- **Start:** `{"MessageType":"HallPassEventsStart","Data":"0,30000"}` or `{"MessageType":"HallPassEventsStart","Data":"0,30000,<afterSeq>"}`. The first two values are Emby's start delay and period; the optional third is the highest `Seq` the app has already seen.
- **Stop:** `{"MessageType":"HallPassEventsStop"}`.
- **Answer:** `{"MessageType":"HallPassEvents","Data":{"ApiLevel":1,"LatestSeq":<n>,"Truncated":false,"Events":[{"Seq":<n>,"Name":"HallPass.ScanFinished","AtUtc":"...Z","Data":{...}}]}}`. The event objects are exactly those of the catch-up route, oldest first.
- **When it is sent:** (a) once right after the subscription: the events after `afterSeq` (at most 100, `Truncated: true` if the log no longer reaches that far), or with no `afterSeq` an empty `Events` list and `LatestSeq` as the baseline; (b) within about a second of every new event, only to subscribed sockets; (c) as a heartbeat with an empty `Events` list every 30 seconds.
- **Who may subscribe:** an administrator. Anyone else gets exactly one message `{"MessageType":"HallPassEvents","Data":{"Error":"Administrator login required."}}` and nothing more.
- **Per connection:** the highest `Seq` already sent is remembered, so nothing is sent twice on one connection. Subscribing again on the same socket replaces the earlier subscription, so `Start` with a new `afterSeq` replays only newer events.
- Implemented in `Services/EventsPushListener.cs` (an Emby `BasePeriodicWebSocketListener`) with the Emby-free logic in `Core/Notifications/EventSubscription.cs`.

## 6. Where each setting lives
| Setting | Lives in | Why |
|---|---|---|
| Which rules exist, thresholds (such as `MinCount`), quiet hours, sound, priority | **The app** (per phone) | decided with the owner for all services |
| "Send phone alerts" master switch (default on) and one switch per alert (default on, **Review queue off**) | **The plugin's settings page**: Settings > Phone alerts | they change what the server sends. An alert that is off is neither sent nor logged |

The saved names are `SendPhoneAlerts`, `AlertScanFinished`, `AlertScanFailed`, `AlertApplyFinished`, `AlertApplyFailed` and `AlertReviewQueue` in `HallPass.xml`.

## 7. For the app session
1. Gate on `Info.Features` containing `events` (and keep the existing gate on `ApiVersion` and `Version`).
2. Build the rules from the catalog. A built-in copy of the table in section 3 is a good fallback for older plugins.
3. Subscribe with `HallPassEventsStart` (section 5b) and handle `HallPassEvents` messages; dedupe by `Seq`. Messages named `HallPass.<Event>` arrive only on address-token sockets; the app does not need them; show the generic `LockScreenText` on the lock screen.
4. Keep the last seen `Seq`; on connect, on app open and from the periodic poll call `Events?After=`; if `Truncated`, read `Status`.
5. `ReviewQueue.Count` always says how many are waiting now, so a missed event is repaired by the next one.

## 8. Verified and not verified
See `docs/HANDOFF.md` section 11 and `PROGRESS.md` (2026-10-14) for what was checked live and what was not.
