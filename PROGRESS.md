# PROGRESS: Hall Pass

## 2026-10-12: started
- Repo created by the owner; design and planning decisions copied into `docs/`. Names and GUID fixed (see CLAUDE.md).
- The design document uses working names (EmbyExplicitTagger, EmbyBulkTagEditor, routes like /ExplicitTagger and /BulkTags): the fixed names in CLAUDE.md replace them everywhere.
- Owner's choices: a full web page in the Emby dashboard in addition to a phone/app UI (the Embidextrous app gets a tab later).


## 2026-10-12: Phase 0 done (hello world plus probe), see docs/API-NOTES.md
- Hello-world plugin `HallPass.dll` 0.1.0.0 builds, loads in Emby 4.10.1.0, and is listed in the dashboard menu (`EnableInMainMenu`, icon `explicit`). It has `GET /HallPass/Info` and two **temporary probe endpoints** (`/HallPass/Probe/Item`, `/HallPass/Probe/Tag`, admin only) used for the checks below; remove them before any release.
- Checked live on the test server (the owner's music in `C:\claude\Music`): the library update path writes tags, the REST API sees them (in `TagItems`, not `Tags`), `Tags=` / `ExcludeTags=` queries work at track level; **a full refresh with "replace all metadata" wiped the tag on an unlocked item and kept it on an item with the `Tags` field locked**; Emby exposes lyrics as subtitle streams (embedded `text` and sidecar `lrc`) and a plugin can read both (`ISubtitleEncoder.GetSubtitles` returns a cache file path); constructor injection of `ILibraryManager`, `IMediaSourceManager` and `ISubtitleEncoder` works.
- Decisions: lyrics come from Emby's streams (no TagLibSharp for lyrics); embedded advisory flags and ISRC need ffprobe or TagLibSharp and are optional for v1 (survey: flags on 3 percent of files); always lock `Tags` when writing; offer `saveMetadata` as a setting.
- Not verified: NFO behaviour with a saver enabled; `ItemAdded`; the scheduled task; `IHttpClient`; the evidence store; batch write speed; SmartLists' own Tags rule on tags written this way.

## Next
- Milestone 1 (Core): decision engine, lyrics profanity scanner, title and folder heuristics, with unit tests; then a dry-run scan over the test library, looking at the results before any online lookups.

## Open questions
- How to share the tag-writing code between this plugin and its companion (separate repos): copy kept in sync, or a git subtree. Decide when the first shared code exists.
