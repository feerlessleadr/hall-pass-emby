# Emby Explicit Content Tagger: Design Document

> Audience: Claude Code. Read this whole document, then create `CLAUDE.md` and `PROGRESS.md` in the repo root before writing code. Do Phase 0 (verification) first and record the findings in `docs/API-NOTES.md`. This plugin is a companion to the Bulk Tag Editor (`emby-bulk-tag-editor-design.md`), and the two share tag-writing logic. Read that doc's Phase 0 table too, since most of the Emby API questions are identical.

## 1. Overview

An Emby Server plugin that scans music tracks, determines whether each one is **explicit**, and applies a configurable tag to the item. The user can then build a smart playlist that excludes the tag, giving a safe "family and friends" playlist with no cursing.

The design priority is **not letting explicit tracks slip through**. A missed explicit track is worse than a falsely flagged clean one. Therefore the plugin tracks three states, not two.

### Goals
- Detect explicit tracks using multiple evidence sources, in priority order, with a confidence score.
- Apply tags at the **track** level (what smart playlists filter on), with optional roll-up to album and artist.
- Distinguish **explicit**, **verified clean**, and **unknown / needs review**, so the family-safe playlist can be as strict as the user wants.
- Provide a review UI with manual overrides that are never overwritten by later scans.
- Scan incrementally (new or changed items only), automatically and on a schedule.

### Non-goals (v1)
- Editing audio files or lyrics.
- Skipping or censoring audio at playback time.
- Detecting non-lyrical content (e.g. violent themes). This is profanity and the explicit flag only.
- Video content (movies, shows). The architecture should allow it later, but ship music only.

## 2. Tag model

Defaults (all configurable in plugin settings):

| Tag | Meaning |
|-----|---------|
| `explicit` | Confidently explicit (flagged by metadata/online source, or lyrics score ≥ threshold, or manual) |
| `clean` | Confidently not explicit (positive evidence, e.g. a "clean/not explicit" flag from a strong match) |
| `explicit-review` | Suspected explicit or inconclusive, needs human review |

Untagged tracks are **unscanned**. Tracks with no evidence either way get `explicit-review` (configurable, see policy below).

### Smart playlist guidance (document in the README)
- **Conservative** (recommended): exclude tag `explicit` **and** exclude tag `explicit-review`.
- **Strict**: include only tracks with tag `clean`.
- **Relaxed**: exclude only `explicit`.

The smart playlist itself is out of scope for this plugin, but the README should show how to build these rules (Emby's smart-playlist feature or a plugin such as a SmartLists port, whichever the user has).

## 3. Phase 0: Verify before building (required)

Re-use the Emby API verification list from the Bulk Tag Editor doc (plugin shell, REST endpoints, update paths, locked fields, DI services, config pages). In addition, using ILSpy / `ilspycmd` on the Emby Server DLLs, determine and record:

| # | Question | Why it matters |
|---|----------|----------------|
| 1 | Which Emby APIs expose audio file tags (ID3 / Vorbis / MP4 atoms)? Does `Audio` or its `MediaStreams` carry arbitrary tag frames, or only a fixed set? | Reading `ITUNESADVISORY` / `rtng` |
| 2 | If not exposed, can the plugin ship and load **TagLibSharp** (or `taglib-sharp-netstandard2.0`) alongside the plugin DLL without assembly-load conflicts? Test it early. | Embedded tag reading |
| 3 | Does Emby expose lyrics to plugins (sidecar `.lrc`/`.txt`, embedded USLT, any `ILyricManager`-style service)? | Lyrics scan |
| 4 | Where are ISRC, MusicBrainz IDs, and duration available (`ProviderIds`, `RunTimeTicks`, file tags)? | Online matching |
| 5 | Item lifecycle events (`ILibraryManager.ItemAdded` / `ItemUpdated`) and how to safely enqueue work from them | Auto-scan new items |
| 6 | `IScheduledTask` registration and progress reporting | Scheduled scans |
| 7 | Which SQLite option loads reliably inside an Emby plugin (e.g. `Microsoft.Data.Sqlite`, the SQLite already shipped by Emby, or LiteDB)? Fall back to JSON only if none work. | Evidence store |
| 8 | HTTP client best practice inside Emby (`IHttpClient`, or `HttpClient`) and how to respect proxies | Online lookups |

If any assumption in this document conflicts with the findings, **the findings win**; update this doc and note it in `PROGRESS.md`.

## 4. Detection pipeline

Evaluate sources in order for each track. Stop early when a decision reaches the auto-decision threshold. Record **every** piece of evidence gathered (source, verdict, confidence) in the evidence store for the review UI.

### Stage 1: Embedded file metadata (cheapest and most reliable)
Read from the audio file's tags (via Emby if exposed, else TagLibSharp):
- **MP4/M4A**: `rtng` atom. 1 or 4 = explicit, 2 = clean, 0 = none.
- **ID3v2 (MP3)**: `TXXX:ITUNESADVISORY` (also try a bare `ITUNESADVISORY` frame). 1 = explicit, 2 = clean, 0 = none. Also `TXXX:EXPLICIT`, `TXXX:RATING` if present.
- **Vorbis/FLAC/Opus**: `ITUNESADVISORY`, `EXPLICIT`, `CONTENTADVISORY` comments. Treat `1`, `true`, `yes`, `explicit` as explicit.
- Make the list of field names user-extensible in settings.
- **Title/album heuristics**: regex for `(Explicit)`, `[Explicit]`, `(E)`, and for clean markers like `(Clean)`, `(Radio Edit)`, `(Censored)`. A title marker counts as medium-confidence evidence, not as final.

Verdicts: `Explicit (1.0)` / `Clean (0.9)` / `None`.

### Stage 2: Online catalog lookup (optional, user-enabled)
Send only artist, title, album, duration, and ISRC. Never send file paths or other user data. Provide an "offline mode" toggle that disables all network stages.

Providers (each a small adapter behind `IExplicitnessProvider`; verify the current API behavior before relying on it):
1. **Deezer** public API: lookup by ISRC (`/track/isrc:{isrc}`), falling back to search. Response includes explicit flags (e.g. `explicit_lyrics`, `explicit_content_lyrics`, with values distinguishing explicit / not explicit / edited / unknown). No auth key required.
2. **iTunes Search API**: search by term and entity=song; the result carries a `trackExplicitness` field (`explicit` / `cleaned` / `notExplicit`). No key required, but it is rate-limited.
3. **Spotify Web API** (optional, requires user-supplied client credentials): lookup by ISRC (`q=isrc:`), with an `explicit` boolean on tracks.

Matching rules (strict, to avoid attaching the wrong version's flag):
- Prefer **ISRC** matches (confidence 0.95).
- Otherwise require normalized-title and primary-artist match plus duration within ±3 s (confidence 0.8). Also compare album when available. Fuzzy-only matches cap at 0.6.
- If providers disagree, take the most conservative verdict (explicit wins) and mark the result for review.
- Respect provider rate limits with a per-provider token bucket, retry with backoff on 429/5xx, and cache results (keyed by ISRC or normalized artist+title+duration) so reruns don't re-query.

Note the "clean version vs explicit version" problem: the same song can exist as both. Matching on ISRC or duration is what makes the verdict specific to the user's file.

### Stage 3: Lyrics profanity scan (fallback, and a safety net)
Lyric sources, in order: sidecar `.lrc` / `.txt`, embedded lyrics (USLT), then an optional online lyrics lookup (for example the free LRCLIB API, matching by artist, title, album, and duration; it is opt-in and subject to the same privacy rule: only metadata is sent).

Scanning rules:
- Strip LRC timestamps and metadata lines, and normalize case and Unicode.
- Match **whole words** from a tiered word list: `severe`, `moderate`, `mild`. Ship a sensible default list as an embedded resource; users can edit tiers, add words, and keep an **allowlist** (e.g. a place name or an artist's stylization).
- Normalize obfuscation: `f*ck`, `f**k`, `sh!t`, repeated letters, simple leetspeak. Handle common inflections (-ing, -ed, -er, -s) via patterns.
- Avoid the "Scunthorpe problem": never substring-match.
- Score: weighted hit count (e.g. severe = 3, moderate = 2, mild = 1), compared with configurable thresholds. For example, score ≥ 3 → `Explicit (0.75)`, score 1-2 → `Suspect (0.5)`.
- **Do not persist lyrics.** Store only the score and the matched terms (so the review UI can show "matched: …").
- Lyrics found with **zero** hits supports `Clean` only at low confidence (0.5), since lyrics may be incomplete or wrong. Lyrics **not found** is no evidence at all.

### Stage 4: Manual override (always wins)
User decisions made in the review UI are stored as `Override(explicit | clean)` and are authoritative. Scans never replace them (unless "reset override" is used).

### Decision logic
```
if override        → use override
else gather evidence from stages 1..3
  best_explicit = max confidence of Explicit verdicts
  best_clean    = max confidence of Clean verdicts
  if best_explicit ≥ T_auto_explicit (default 0.75)         → EXPLICIT
  elif best_clean  ≥ T_auto_clean    (default 0.9) and best_explicit == 0 → CLEAN
  else                                                       → REVIEW
```
Conflicts (both explicit and clean evidence present) go to **REVIEW** unless the explicit evidence is ≥ 0.95, in which case EXPLICIT wins. All thresholds are configurable. A "paranoid" preset lowers `T_auto_explicit` and requires multiple sources for CLEAN.

Instrumental / classical handling: setting `SkipGenres[]` (e.g. Classical, Soundtrack, Instrumental) and `SkipLibraries[]`. Skipped items are recorded as `Skipped` and optionally tagged `clean` per setting `TagSkippedAsClean` (default off).

## 5. Applying tags
- A `TagApplier` takes `(item, decision)` and sets exactly one of the three managed tags (`explicit` / `clean` / `explicit-review`), **removing the other two** so states never coexist. Non-managed tags are never touched.
- Uses the same Emby write path and Tags field lock as the Bulk Tag Editor (shared code), so Emby metadata refreshes don't undo them.
- No-op detection: don't write if the item already has exactly the right managed tag.
- **Roll-up** (optional settings): tag an **album** `explicit` if any track is explicit (and `explicit-review` if none but some are in review), and likewise for the artist. Roll-ups use a separate tag name setting (default same names) and are recalculated after scans.
- **Journal** of changes (item, before, after, reason) for audit, exposed in the UI and kept N days. (Reuse the Bulk Tag Editor journal design if sharing code; otherwise a simpler append-only log is enough.)

## 6. Evidence store

SQLite (per Phase 0; JSON fallback), in the plugin data folder.

```
TrackEvidence
  ItemId          TEXT PRIMARY KEY
  FileKey         TEXT      -- path + size + mtime hash, for change detection
  Status          TEXT      -- Explicit | Clean | Review | Skipped | Unscanned
  Decision        TEXT      -- same enum, after override
  Override        TEXT NULL -- Explicit | Clean | NULL
  Confidence      REAL
  LastScannedUtc  TEXT
  Evidence        TEXT      -- JSON array: [{source, verdict, confidence, detail}]
OnlineCache
  Key             TEXT PRIMARY KEY   -- ISRC or normalized artist|title|duration
  Provider        TEXT
  ResultJson      TEXT
  FetchedUtc      TEXT
```
- Rescan only when `FileKey` changes, when a user forces it, or when settings that affect the decision change (store a `SettingsVersion` hash).
- Cache TTL for online results (default 90 days); negative results (no match) get a shorter TTL (default 14 days).

## 7. Triggers and scheduling
- **Scheduled task** "Scan for explicit content": the default is daily, incremental. Reports progress and supports cancel. It runs with limited concurrency (default 2 workers), and online providers are throttled independently.
- **Auto-scan on add**: on item-added events, enqueue the track into a bounded in-memory queue handled by a background worker. It debounces, because bulk imports can produce thousands of events, and it must never run heavy work on the event thread.
- **On-demand**: a "Scan now" button for all, for a selected library, or for an artist/album, with "force rescan".
- First-run flow: a **dry-run mode** (default **on** for the first scan) records decisions without writing tags, then shows a summary so the user can sanity-check before enabling writes.

## 8. REST API (confirm mechanism in Phase 0; admin-only)

| Method | Route | Purpose |
|--------|-------|---------|
| POST | `/ExplicitTagger/Scan` | `{scope, force, dryRun}` → `{jobId}` |
| GET | `/ExplicitTagger/Jobs/{id}` | progress and counts |
| POST | `/ExplicitTagger/Jobs/{id}/Cancel` | cancel |
| GET | `/ExplicitTagger/Review` | paged list; filters: status, source, artist, text, confidence range |
| GET | `/ExplicitTagger/Items/{id}` | full evidence for one track |
| POST | `/ExplicitTagger/Items/Decide` | `{itemIds[], decision: explicit|clean|clearOverride}` |
| GET | `/ExplicitTagger/Stats` | totals by status, by source, unscanned count |
| POST | `/ExplicitTagger/Apply` | write tags from stored decisions (used after a dry run) |
| POST | `/ExplicitTagger/Test` | `{artist,title,...}` → shows what each stage would say (debug / settings tuning) |

## 9. UI

Plugin page with tabs:
1. **Dashboard**: counts (Explicit / Clean / Review / Skipped / Unscanned), the last scan, a progress bar, and **Scan now** / **Apply decisions** buttons, with a dry-run indicator.
2. **Review queue**: a table of tracks (artist, title, album, status, confidence, evidence summary such as "Deezer: explicit; lyrics: 4 hits (…)"), with filters and bulk actions **Mark explicit / Mark clean / Clear override**. A row expand shows the full evidence list. Include a link to open the track in Emby.
3. **Settings**:
   - Tag names, roll-up options, and the decision policy for unknowns.
   - Thresholds with a "Conservative / Balanced / Relaxed" preset.
   - Source toggles (embedded, Deezer, iTunes, Spotify + credentials, lyrics, online lyrics) and an **Offline mode**.
   - Metadata field names to check.
   - Skip genres and libraries.
   - Word list editor: tiers, allowlist, and a **Test text** box.
4. **Test lookup**: enter artist and title and see the pipeline verdict per stage.
5. **Log / journal**.

Spotify credentials are stored in plugin config and are never logged or returned to the client in full.

## 10. Privacy, ToS, and safety
- Network stages are opt-in, and the settings page lists exactly what is sent (artist, title, album, duration, ISRC).
- Respect each provider's terms and rate limits. Identify the client with a descriptive User-Agent. Cache aggressively. This is for personal library management.
- Do not store lyrics text. Do not log secrets.
- Fail safe: a network failure or provider error means that stage yields no evidence (never `Clean`), and the item ends up in REVIEW rather than being tagged clean.

## 11. Project structure

```
EmbyExplicitTagger/
├─ src/EmbyExplicitTagger/
│  ├─ Plugin.cs, PluginConfiguration.cs
│  ├─ Api/ (ExplicitApi.cs, Dtos.cs)
│  ├─ Core/                          # pure logic, unit-testable
│  │  ├─ DecisionEngine.cs
│  │  ├─ Evidence.cs
│  │  ├─ ProfanityScanner.cs  (+ WordListLoader)
│  │  ├─ TitleHeuristics.cs
│  │  ├─ TrackMatcher.cs              # normalization, fuzzy and duration matching
│  │  └─ TokenBucket.cs
│  ├─ Sources/
│  │  ├─ EmbeddedTagReader.cs
│  │  ├─ LyricsProvider.cs
│  │  ├─ IExplicitnessProvider.cs
│  │  ├─ DeezerProvider.cs, ITunesProvider.cs, SpotifyProvider.cs
│  ├─ Services/
│  │  ├─ ScanJobRunner.cs, ScanQueue.cs, EvidenceStore.cs
│  │  ├─ TagApplier.cs, RollupService.cs
│  │  └─ ExplicitScanTask.cs          # IScheduledTask
│  ├─ Resources/ default-wordlist.json
│  └─ Web/ (explicit.html, explicit.js)
├─ shared/ (tag-writing helpers shared with the Bulk Tag Editor via linked sources)
├─ tests/EmbyExplicitTagger.Tests/
├─ docs/API-NOTES.md, docs/TESTING.md
├─ CLAUDE.md, PROGRESS.md
```

## 12. Testing
- **Unit tests (no Emby)**:
  - `DecisionEngine`: every branch (override, conflict, thresholds, unknown policy, skip rules).
  - `ProfanityScanner`: whole-word matching, obfuscation, inflections, Scunthorpe cases, the allowlist, LRC stripping, tier scoring.
  - `TrackMatcher`: normalization ("feat." variants, punctuation, remaster suffixes), duration tolerance, ISRC preference.
  - `TitleHeuristics`.
  - Provider adapters against **recorded JSON fixtures** (no live network in tests).
- **Golden set**: a small CSV in `tests/fixtures` of well-known tracks with expected verdicts, used for manual and semi-automated regression runs.
- **Manual integration** (`docs/TESTING.md`): a test library with known explicit/clean/instrumental tracks, a dry run first, then a real run. Check the tags, that managed tags never coexist, that overrides survive rescans, that refresh doesn't strip the tags, and that offline mode makes zero network calls.

## 13. Milestones

| M | Deliverable | Done when |
|---|-------------|-----------|
| 0 | `API-NOTES.md`; hello-world plugin loads; a decision on TagLibSharp vs Emby-native tag reading | Plugin loads; one file's `ITUNESADVISORY` read successfully |
| 1 | `Core/`: DecisionEngine, ProfanityScanner, TrackMatcher plus unit tests | Tests pass |
| 2 | Stage 1 (embedded) plus EvidenceStore plus dry-run scan of a library | Dashboard counts reflect real embedded flags |
| 3 | TagApplier (shared writer), tag locking, apply from stored decisions | Tags appear in Emby and survive a refresh |
| 4 | Review UI plus manual overrides | Overrides persist across rescans |
| 5 | Stage 3: lyrics scan (local sources) | Suspect tracks appear in review with matched terms |
| 6 | Stage 2: Deezer plus iTunes providers, cache, rate limiting | ISRC-matched results appear; offline mode verified |
| 7 | Scheduled task plus auto-scan on add; roll-up; Spotify and online lyrics (optional) | New imports get tagged unattended |
| 8 | Hardening on a large library, README with smart playlist recipes | 50k+ tracks scanned without issues |

## 14. Open questions (resolve and record)
1. Can Emby's smart playlists (or the user's installed smart-playlist plugin) filter on **tags at the track level** and re-evaluate automatically when tags change? Document the exact rule setup for the three policies.
2. Which embedded fields does the user's library actually contain? Sample 20 files from different sources (purchased, ripped, downloaded via the arrs) and report which of `rtng` / `ITUNESADVISORY` is present. This determines how much work stages 2-3 need to do.
3. Should an unscanned track be treated as unsafe in the family playlist? (It will be treated as safe by a "NOT explicit" rule. Mention in the README that the user should run a full scan before trusting a playlist.)
4. Does the user want video support (movies and shows, via content ratings) later? If yes, keep `TagApplier` and `EvidenceStore` item-type agnostic.
