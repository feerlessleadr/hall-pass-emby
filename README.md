<h1 align="center">Hall Pass (for Emby)</h1>

An Emby Server plugin that works out which of your music tracks are explicit and tags them, so a smart playlist can leave them out. It reads the lyrics Emby already has, can ask Deezer and iTunes (only if you allow it), keeps a review queue for the doubtful ones, and changes nothing in your library until you say so.

> **Status: tested on Emby Server 4.10.1.0 only.** It was developed and checked against a test server with a small music library. Start with the dry run (it is the default) and read what it would do before you let it write tags. Every tag change is recorded and can be undone.

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
1. **Lyrics** (main source). Emby shows embedded lyrics and `.lrc` files as subtitle streams; Hall Pass reads them from Emby (and reads a `.lrc` or `.txt` file with the track's name from its folder when Emby lists none) and counts profanity in three tiers (severe, moderate, mild) with whole-word matching, inflections and masked spellings (`f*ck`, `sh!t`). Mild words (damn, hell, crap) are noted but never score. **Each lyric source is judged on its own**: when the lyrics embedded in the file and a `.lrc` file disagree, the track goes to review instead of one of them quietly winning. A few words with an everyday meaning are handled with care: "cocked" is not "cock", a capitalised "Dick" in the middle of a line is read as a name, and a single use of "dick" or "cock" is only a suspect. You can add, move or ignore words in Settings (the built-in list is `src/HallPass/Resources/default-wordlist.json`). Lyrics are never stored: only the result and the words that matched.
2. **The file's own tags**, read with Emby's own `ffprobe`: an explicit/clean flag when a file has one, plus the ISRC and album barcode used for online lookups.
3. **Names**: profanity in the track's title (a severe word is strong evidence), and markers such as "(Explicit)", "(Clean)", "(NSFW)" in the title, album or folder names (only a hint).
4. **Online catalogs** (off by default): Deezer by ISRC (or by artist and title) and iTunes by album barcode, and, for tracks with no lyrics of their own, the lyrics catalog LRCLIB (a separate switch). See *Privacy*.
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
   Use **Select all N matching** to cover every page, or switch **Show** to Albums or Artists to mark a whole album or artist at once, and sort by **Most played first** to decide what you actually listen to first.
3. On the Dashboard press **Preview changes**: it lists which tracks would get which tag (and what their tags are now). Dry run is on, so nothing has changed yet.
4. When you are happy: **Settings > Tags & writing**, untick *Dry run*, save, then press **Apply decisions** on the Dashboard. You do not need to scan again, and you do not have to finish the review first: tracks still in review get the `explicit-review` tag.
5. Changed your mind? **History** lists every apply with an **Undo** button. A track whose tags somebody edited since is left alone unless you tick the force box.

## Keeping it up to date
- A **scheduled task** (*Dashboard > Scheduled Tasks > Hall Pass*) runs every night at 3:30 and looks only at new or changed tracks. Change its schedule there like any other task.
- **New tracks** are looked at by themselves a little after Emby adds them (switchable).
- By default both only record decisions. Switch on *Write tags after a scheduled or automatic scan* (with dry run off) if you want them to write tags too; each change is journalled.

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
Everything stays on your server unless you turn on **Online lookups** (Settings; off by default, with a confirmation). Then Hall Pass asks Deezer (`api.deezer.com`) and Apple iTunes (`itunes.apple.com`) about tracks and sends **only** the artist, title, ISRC or album barcode: never file names, paths, lyrics, or anything about you or your server. Answers are kept on the server (90 days; 14 days for "nothing found") so nothing is asked twice, requests are rate limited (Deezer 8 a second, iTunes 20 a minute) and a failed request never makes a track look clean. If you also tick the LRCLIB lyrics lookup, Hall Pass asks lrclib.net for the lyrics of tracks that have none, sending only the artist, title, album and length; it scans that text in memory and never keeps it. The Try it tab contacts these services only when you press its button, and sends only what you type there. Turn the option off and nothing leaves the server.

## What it changes and what it never does
- It changes **only** the four tags above (in Emby's database, and in NFO files if Emby's metadata savers are on for your library; there is a setting to skip the savers) and locks the track's Tags field.
- It never edits audio files, never stores lyrics, never deletes anything, and never writes while *Dry run* is on.
- Its own files are in Emby's `plugins/configurations` folder: `HallPass.xml` (settings), `HallPass.Evidence.json` (results), `HallPass.OnlineCache.json` (kept answers), `HallPass.Journal/` (the change journal) and, for phone alerts, `HallPass.Events.json` (the last 500 alerts), `HallPass.Events.json.seq` and `HallPass.Notifications.json`.
- To remove it: delete `HallPass.dll` and restart Emby. Tags it wrote stay until you remove them (undo from History first if you want them gone).

## Settings in short
Settings has six sections. **Scoring**: preset (Balanced, Conservative, Relaxed), the likely-clean confidence, the fewest lyric words that can show a track is clean, a per-word cap, skipped genres. **Word list**: your own additions, moves, ignored and always-fine words, the built-in list, and a box to try some text. **Lookups**: file tags, online lookups (Deezer, iTunes, how long answers are kept) and the LRCLIB lyrics lookup. **Tags & writing**: the four tag names, dry run, metadata savers, skipped genres as clean, batch size, and optionally tagging albums and artists that contain an explicit track. **Keeping up to date**: the nightly task, new tracks, write after scan. **Phone alerts**: what the server may tell the Embidextrous app (see below). The **Try it** tab looks up one track you describe, and every review row has a **Rescan this track** button. Saving re-decides every track from the stored evidence at once; options that shape the evidence (genres, lyrics options, online) need **Rescan everything** to reach tracks already scanned.

## Phone alerts
Since 1.4.0.0 Hall Pass can alert the [Embidextrous](https://github.com/feerlessleadr/embidextrous) app on your phone. It uses Emby itself (the websocket that the app already holds open as an administrator) and no outside service. Five alerts exist: **a scan finished**, **a scan did not finish**, **tags were written or put back**, **writing tags did not finish** (also sent when the server stopped in the middle of a job), and **more tracks are waiting for your review**. A scan you start always reports. Scans and writing that happen by themselves (the nightly scan, new tracks) report only when they changed 25 or more tracks, so a few new tracks make no noise. The app decides which alerts you see and when; **Settings > Phone alerts** has a master switch and one switch per alert that decide what the server sends at all (the review alert is off by default). An alert that is switched off is not sent. No alert ever contains a file path, and none contains a track or artist name. The details for developers are in [docs/NOTIFICATIONS.md](docs/NOTIFICATIONS.md).

## Known limits
- Tested on Emby Server 4.10.1.0 with a small library. Not tested on large libraries, with NFO savers switched on, or when Emby stops in the middle of a write (the journal then shows the job as unfinished).
- Lyrics decide most tracks, so tracks without lyrics (many FLAC files) end up in review unless a catalog (or the optional LRCLIB lookup) knows them.
- Words with an everyday meaning cannot always be told apart from the slang: a lowercase "dick" used as a name still counts (as a suspect), and when two lyric transcriptions disagree you get a review, not a verdict.
- Catalogs are not always right, and lyrics can be missing or wrong: that is why disagreements go to review.
- iTunes allows about 20 questions a minute, so a first scan with iTunes on is slow for a big library.
- English-language profanity only.

## Building from source
    dotnet build src/HallPass -c Release          # src/HallPass/bin/Release/net6.0/HallPass.dll
    dotnet test tests/HallPass.Tests                # the engine's unit tests (net8.0)

See [TESTING.md](TESTING.md) for testing against an Emby server, [docs/DESIGN.md](docs/DESIGN.md) for the original design, [docs/API-NOTES.md](docs/API-NOTES.md) for what was learned about Emby's plugin API, and [PROGRESS.md](PROGRESS.md) for the build log.

Phone app: the **Library > Hall Pass** tab of [Embidextrous](https://github.com/feerlessleadr/embidextrous) reviews tracks, albums and artists from your phone, including a swipe mode (needs this plugin 1.2.0.0 or newer; the phone alerts need 1.4.0.0 or newer).

Companion plugin (planned): [Tag Team](https://github.com/feerlessleadr/tag-team-emby), a bulk tag editor. Made by the same author as [Hall Monitor](https://github.com/feerlessleadr/hall-monitor-emby) and the Embidextrous phone app.

## License
[MIT](LICENSE).
