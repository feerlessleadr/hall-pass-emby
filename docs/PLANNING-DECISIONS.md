# Decisions: the two tagging plugins

## 2026-10-12: names (owner's choice)
- **Tag Team**: the Bulk Tag Editor (`emby-bulk-tag-editor-design.md`).
- **Hall Pass**: the Explicit Content Tagger (`emby-explicit-tagger-design.md`). It pairs with the Hall Monitor naming (a track gets a pass if it is clean).
- Dropped after a web and GitHub check (not legal advice, no direct trademark database access): Bleep (Bleep.com record shop, a pending BLEEPED trademark in the same field), Swear Jar (a SWEAR JAR software trademark filing in the same field, many projects), Clean Cut (two same-purpose apps with near-identical names: CleanCut for Apple TV, Clean Cuts for Mac). Hall Pass has a near-neighbour (an iOS time-limit app named HallPass, May 2026) and an unclear trademark filing for mobile software; Tag Team had no conflict found.
- Fix the names everywhere on day one (DLL, namespace, routes, settings files, repo): a rename later is costly (see Hall Monitor).

## 2026-10-12: findings from the music survey (116 unique files from `V:\Music`)
- Embedded advisory flags are on only 4 of 116 files (1 explicit, 3 "not explicit"); no iTunes advisory atom in the 2 M4A files. Stage 1 of the Explicit Tagger is a footnote.
- Embedded lyrics on 90 of 116 (88 of 99 MP3, 2 of 15 FLAC): the lyrics scan is the main stage; FLACs need an online lyrics lookup.
- MusicBrainz release-track id on 110, an ISRC on 71 or more (12 `isrc` tags, 59 MP3 `TSRC` frames), an album barcode on 77: ISRC and UPC lookups are promising for an authoritative online flag (APIs still to be verified in Phase 0).
- Album folders use "(nsfw)" and "(sfw)" markers (mgk): a cheap heuristic.
- Library probably on the order of a thousand-plus tracks (rough, from sample overlap), not 50k.
- Plan: build the shared tag writer and Hall Pass first (lyrics scan, decision logic, dry run), look at the results, then add online lookups; Tag Team (general bulk editor) after that. Engine plus API in the plugin, main UI possibly in the Embidextrous Tags tab (owner to decide).
