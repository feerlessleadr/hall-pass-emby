# Hall Pass: decision log

Append-only, newest at the bottom. Never rewrite an entry; if a decision is reversed, add a new entry that references the old one. Earlier decisions already live in `docs/PLANNING-DECISIONS.md` (names, music survey), `PROGRESS.md` (dated milestone log with owner decisions), `docs/HANDOFF.md` (consolidated lessons) and `docs/API-NOTES.md` (Phase 0 findings). Do not duplicate them; add pointers.

### 2026-10-12, Names fixed on day one
- **Context:** Candidate names Bleep, Swear Jar and Clean Cut had trademark or near-name conflicts.
- **Options considered:** Those three; Hall Pass (pairs with Hall Monitor); Tag Team for the bulk editor.
- **Decision:** Product Hall Pass, DLL `HallPass.dll`, routes `/HallPass/...`, settings `HallPass.xml`, GUID `6ca80343-2b60-40de-ab81-7a41628fcaf2`. Never renamed.
- **Rationale:** A rename later means a new DLL name, new settings files and new routes. Source: `docs/PLANNING-DECISIONS.md`.
- **Impact:** Embidextrous, Tag Team, SmartLists docs all use these names.

### 2026-10-12, Four tags, dry run by default, nothing replaces a manual decision
- **Context:** Smart playlists need a simple way to exclude explicit music.
- **Options considered:** One explicit flag; several evidence-based states.
- **Decision:** Exactly one of `explicit`, `clean`, `likely-clean`, `explicit-review` per track. Dry run is on by default, every write is journaled and undoable, manual decisions are never replaced by scans, and conflicts go to a person. Online lookups (Deezer, iTunes, LRCLIB) are opt-in.
- **Rationale:** Protect the owner's library from silent mass changes; wrong tags on a child-facing library matter. Details: `PROGRESS.md` dated 2026-10-12.
- **Impact:** SmartLists rules exclude by tag; Tag Team will edit the same tags.

### 2026-10-12, Phase 0 verified Emby's tag API before design
- **Context:** The REST `Tags` field is empty; the real list is `TagItems`.
- **Options considered:** Trust the design doc; probe the server.
- **Decision:** Probe first. Persist via `ILibraryManager.UpdateItems(..., MetadataEdit, ...)` and lock `MetadataFields.Tags`, or a full metadata refresh wipes tags.
- **Rationale:** Verified live. Findings win over the design. Source: `docs/API-NOTES.md`.
- **Impact:** Shared with Tag Team and Label Maker; code-sharing approach is still undecided.

### 2026-10-14, Phone alerts through a websocket subscription, not SendMessageToAdminSessions (1.4.0.0)
- **Context:** The app connects with its token in a header, so Emby does not link that socket to an admin session and `SendMessageToAdminSessions` never reaches it.
- **Options considered:** Rely on admin-session messages; poll only; subscribe over the Emby websocket.
- **Decision:** Client sends `HallPassEventsStart` with `0,30000,<afterSeq>,<admin token>`. The plugin validates the token with `ISessionManager.GetSessionByAuthenticationToken` plus an administrator check, and re-checks every push. A REST catch-up (`Events?After=`) and a 500-event ring log back it up. Event changes must update catalog, samples and `docs/NOTIFICATIONS.md` together (a test enforces it). No file paths in payloads.
- **Rationale:** Works with header-token sockets and never puts the token in an address. See `docs/HANDOFF.md` section 11.
- **Impact:** Embidextrous `docs/push-contract.md` (shared shape) and its Hall Pass tab. Hall Monitor uses the plugin key in the message instead; the two approaches differ on purpose.

### 2026-11-03, Hall Monitor reference is now hall-monitor-emby
- **Context:** `CLAUDE.md` here names `C:\dev\watching-eye` as the Hall Monitor reference plugin.
- **Options considered:** Edit the body; add a note.
- **Decision:** Add a note in the new persistent-docs block only. `C:\dev\watching-eye` is deprecated; `C:\dev\hall-monitor-emby` is canonical.
- **Rationale:** Avoid rewriting an owner-maintained file.
- **Impact:** Other repos with the stale reference: Embidextrous docs.
