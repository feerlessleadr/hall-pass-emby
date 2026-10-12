<h1 align="center">Hall Pass (for Emby)</h1>

An Emby Server plugin that works out which of your music tracks are explicit and tags them, so a smart playlist can leave them out. It reads the lyrics Emby already has, can ask Deezer and iTunes (only if you allow it), keeps a review queue for the doubtful ones, and changes nothing in your library until you say so.

> **Status: first release, tested on Emby Server 4.10.1.0 only.** It was developed and checked against a test server with a small music library. Start with the dry run (it is the default) and read what it would do before you let it write tags. Every tag change is journalled and can be undone.

## What it does
For every music track Hall Pass collects evidence and decides one of five things:

| Result | Meaning | Tag it gets (names are settings) |
|--------|---------|-----------------------------------|
| **Explicit** | Strong sign the track has explicit content (lyrics with strong words, a catalog or the file says explicit) | `explicit` |
| **Clean** | At least two independent signs it is clean (for example lyrics with no scoring words **and** a catalog that says not explicit), or the file itself says clean, or you decided so | `clean` |
| **Likely clean** | One sign it is clean (usually: the lyrics have no scoring words) and nothing explicit turned up | `likely-clean` |
| **Needs review** | Not enough evidence, or the sources disagree: look at it yourself | `explicit-review` |
| **Skipped** | In a genre you excluded (instrumental music has no lyrics to judge) | none |

A track carries **at most one** of these four tags. Any other tag on it is left alone, and the track's **Tags field is locked** so a metadata refresh cannot undo the result.

### Where the evidence comes from
1. **Lyrics** (main source). Emby shows embedded lyrics and `.lrc` files as subtitle streams; Hall Pass reads them from Emby and counts profanity in three tiers (severe, moderate, mild) with whole-word matching, inflections and masked spellings (`f*ck`, `sh!t`). Mild words (damn, hell, crap) are noted but never score. The word list is `src/HallPass/Resources/default-wordlist.json`. Lyrics are never stored: only the result and the words that matched.
2. **The file's own tags**, read with Emby's own `ffprobe`: an explicit/clean flag when a file has one, plus the ISRC and album barcode used for online lookups.
3. **Names**: markers such as "(Explicit)", "(Clean)", "(NSFW)" in the title, album or folder names. Only a hint.
4. **Online catalogs** (off by default): Deezer by ISRC (or by artist and title) and iTunes by album barcode. See *Privacy*.
5. **You**: a manual decision in the review queue always wins and is never replaced by a scan.

An online "not explicit" is treated as weak evidence (catalogs often miss older songs), so on its own it only makes a track *likely clean*.

## Install
**Through the GitHub Repo Plugin Install plugin** (recommended): add `https://github.com/feerlessleadr/hall-pass-emby` as a repository, let it install `HallPass.dll`, and restart Emby.

**By hand:** download `HallPass.dll` from the [latest release](https://github.com/feerlessleadr/hall-pass-emby/releases/latest), put it in Emby's `plugins` folder and restart Emby. To check the download is the one built from this repository (needs GitHub's `gh` tool):

    gh attestation verify HallPass.dll --repo feerlessleadr/hall-pass-emby

After the restart, **Hall Pass** appears in the dashboard under *Advanced*.

## First run
1. Open **Hall Pass > Dashboard** and press **Scan now**. Nothing is written; decisions are recorded and the counts appear.
2. Look at the **Review** tab: filter by *Needs review* or *Explicit*, read the evidence lines, and mark tracks explicit or clean where you know better.
3. On the Dashboard press **Preview changes**: it lists which tracks would get which tag (and what their tags are now). Dry run is on, so nothing has changed yet.
4. When you are happy: **Settings > Writing tags**, untick *Dry run*, save, then press **Apply decisions** on the Dashboard.
5. Changed your mind? **History** lists every apply with an **Undo** button. A track whose tags somebody edited since is left alone unless you tick the force box.

## Keeping it up to date
- A **scheduled task** (*Dashboard > Scheduled Tasks > Hall Pass*) runs every night at 3:30 and looks only at new or changed tracks. Change its schedule there like any other task.
- **New tracks** are looked at by themselves a little after Emby adds them (switchable).
- By default both only record decisions. Switch on *Write the tags after a scheduled or automatic scan* (with dry run off) if you want them to write tags too; each change is journalled.

## Smart list recipes (checked against the SmartLists plugin)
In a smart list on **Tags**:

| Playlist | Rule | Notes |
|----------|------|-------|
| Strict (only confirmed clean) | `Tags` **Equal** `clean` | |
| Balanced (recommended) | `Tags` **Contains** `likely-clean` (plus a second rule or list for `clean`) | Tracks whose lyrics have no scoring words and where nothing explicit turned up. |
| Conservative (leave out anything doubtful) | `Tags` **NotContains** `explicit` | "Contains" is a substring match, so this also leaves out `explicit-review`. |
| Relaxed (leave out only what is certain) | `Tags` **NotEqual** `explicit` | The whole tag only, so `explicit-review` tracks stay in. |

Scan and apply before you trust a playlist: a track with no tag at all passes any "not" rule.

## Privacy
Everything stays on your server unless you turn on **Online lookups** (Settings; off by default, with a confirmation). Then Hall Pass asks Deezer (`api.deezer.com`) and Apple iTunes (`itunes.apple.com`) about tracks and sends **only** the artist, title, ISRC or album barcode: never file names, paths, lyrics, or anything about you or your server. Answers are kept on the server (90 days; 14 days for "nothing found") so nothing is asked twice, requests are rate limited (Deezer 8 a second, iTunes 20 a minute) and a failed request never makes a track look clean. Turn the option off and nothing leaves the server.

## What it changes and what it never does
- It changes **only** the four tags above (in Emby's database, and in NFO files if Emby's metadata savers are on for your library; there is a setting to skip the savers) and locks the track's Tags field.
- It never edits audio files, never stores lyrics, never deletes anything, and never writes while *Dry run* is on.
- Its own files are in Emby's `plugins/configurations` folder: `HallPass.xml` (settings), `HallPass.Evidence.json` (results), `HallPass.OnlineCache.json` (kept answers) and `HallPass.Journal/` (the change journal).
- To remove it: delete `HallPass.dll` and restart Emby. Tags it wrote stay until you remove them (undo from History first if you want them gone).

## Settings in short
Preset (Balanced, Conservative, Relaxed), the likely-clean confidence, the fewest lyric words that can show a track is clean, a per-word cap, skipped genres, the four tag names, what Hall Pass looks at (file tags, online lookups, which catalogs, how long answers are kept), keeping up to date (new tracks, write after scan), and writing (dry run, metadata savers, skipped genres as clean, batch size). Saving re-decides every track from the stored evidence at once; options that shape the evidence (genres, lyrics options, online) need **Rescan everything** to reach tracks already scanned.

## Known limits
- Tested on Emby Server 4.10.1.0 with a small library. Not tested on large libraries, with NFO savers switched on, or when Emby stops in the middle of a write (the journal then shows the job as unfinished).
- Lyrics decide most tracks, so tracks without lyrics (many FLAC files) end up in review unless a catalog knows them.
- Catalogs are not always right, and lyrics can be missing or wrong: that is why disagreements go to review.
- iTunes allows about 20 questions a minute, so a first scan with iTunes on is slow for a big library.
- English-language profanity only.

## Building from source
    dotnet build src/HallPass -c Release          # src/HallPass/bin/Release/net6.0/HallPass.dll
    dotnet test tests/HallPass.Tests                # the engine's unit tests (net8.0)

See [TESTING.md](TESTING.md) for testing against an Emby server, [docs/DESIGN.md](docs/DESIGN.md) for the original design, [docs/API-NOTES.md](docs/API-NOTES.md) for what was learned about Emby's plugin API, and [PROGRESS.md](PROGRESS.md) for the build log.

Companion plugin (planned): [Tag Team](https://github.com/feerlessleadr/tag-team-emby), a bulk tag editor. Made by the same author as [Hall Monitor](https://github.com/feerlessleadr/hall-monitor-emby) and the Embidextrous phone app.

## License
[MIT](LICENSE).
