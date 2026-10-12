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


## 2026-10-12: milestone 1 done: the engine, 105 unit tests, a dry-run probe
- `src/HallPass/Core` (plain C#, no Emby types): `DecisionEngine` (override, skip, thresholds 0.75 explicit / 0.9 clean, conflict rule, Balanced / Conservative / Relaxed presets, a review reason: NoEvidence, Suspected, Conflict, WeakClean), `ProfanityScanner` (LRC timing and metadata stripping, NFKC and case folding, whole words only, inflections, masked and leet spellings, stretched letters, compounds through stems, allowlist, tier weights 3/2/1, optional per-term cap) with the default word list embedded as `Resources/default-wordlist.json`, `TitleHeuristics` ((Explicit), (Clean), (Radio Edit), (NSFW), (SFW) in title, album and folder names), `TrackMatcher` (ISRC 0.95, title and artist and length 0.8, album +0.05, another length 0.4, fuzzy capped at 0.6). Tests in `tests/HallPass.Tests` (net8.0, links the Core sources): 105 pass (`dotnet test tests/HallPass.Tests`).
- Temporary `GET /HallPass/Probe/Scan` (admin): a dry run over the audio library (reads the lyric streams, applies the engine, writes nothing, returns decisions without lyrics). On the test library (61 tracks, of which 12 are synthetic "Test Artist" files from other sessions): **7 Explicit, 7 Suspected, 32 WeakClean (lyrics found, no hits, 76 to 599 words), 3 no usable text (instrumentals, 0 to 6 words), 12 NoEvidence (the synthetic files)**. Lyrics were found for 49 tracks; the scan of 61 tracks took 3 seconds.
- Finding: with the design's thresholds nothing ever becomes Clean from lyrics alone (0.5 < 0.9), so a playlist that excludes Explicit and Review would be empty until online evidence exists. Needs the owner's decision (see Open questions).

## Open questions (owner)
- How should lyrics-only "clean" tracks be treated: a separate `likely-clean` tag (recommended: playlists choose their strictness), lyrics-only clean allowed, or online confirmation required?
- Do mild words alone (damn, hell, piss, crap, ass) make a track explicit? Today three or more mild hits score 3 and mean explicit ("damn" x4, "piss" x3 in the test set). Options: as it is, cap repeats per term, or mild words alone never go above Suspected.
- How to share the tag-writing code between this plugin and its companion (separate repos): copy kept in sync, or a git subtree. Decide when the first shared code exists.
