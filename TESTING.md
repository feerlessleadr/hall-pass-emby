# Testing Hall Pass

## Unit tests (no Emby needed)
The engine (`src/HallPass/Core`: decision logic, lyrics scanner, title markers, track matching, tag plan, journal rules, evidence store, online parsers and matcher, rate limiter, cache, pending queue) is plain C# with no Emby types and is compiled straight into the test project.

    dotnet test tests/HallPass.Tests

The tests use recorded JSON from Deezer and iTunes (no network) and a fake clock where time matters. CI (`.github/workflows/ci.yml`) runs the same build and tests on every push.

## Against an Emby server
Everything that touches Emby (reading lyric streams, writing tags, the scheduled task, library events, the web page) was checked live on a **test** server, never a production one. To repeat that:
1. `dotnet build src/HallPass -c Release`, copy `src/HallPass/bin/Release/net6.0/HallPass.dll` to the test server's `plugins` folder and restart Emby. (Do not leave an older copy of the DLL with another file name next to it: two copies of one plugin stop Emby loading it.)
2. Put a few dozen music files in a library (mixed MP3 and FLAC, some with lyrics, a few you know are explicit).
3. **Hall Pass > Dashboard > Scan now**, then look at **Review**. Press **Preview changes**.
4. To test writing: untick *Dry run* in Settings, press **Apply decisions**, check the tags in Emby (`GET /Items?Ids=...&Fields=Tags,TagItems`: the tags are in `TagItems`), run a full "replace all metadata" refresh on a tagged track and check the tag is still there, then use **History > Undo** and check the tags are back as they were.
5. A Smart list rule `Tags Contains likely-clean` (or `NotContains explicit`) should list the matching tracks (see the README for the recipes).
6. The nightly task shows under *Dashboard > Scheduled Tasks > Hall Pass*; run it by hand to try it. To try the automatic scan, copy a music file into a library folder and refresh the library: about 40 seconds later it appears in Hall Pass.

## Page script check
After any change to `src/HallPass/HallPass.HallPassConfiguration.js` or its HTML run `powershell -ExecutionPolicy Bypass -File tools\Check-PageScript.ps1`. It loads the script in headless Chrome over the real markup; a script that does not parse makes the page silently refuse to open in Emby, which the unit tests cannot see.

## Phone alerts (live)
`tools/Test-HallPassNotifications.ps1` checks the alerts for real on the local test server (see CLAUDE.md for the server). Build first (`dotnet build src/HallPass -c Release`), then run `powershell -ExecutionPolicy Bypass -File tools\Test-HallPassNotifications.ps1`. It backs up the plugin DLL, `HallPass.xml`, the results, the online cache and the journal; takes five review tracks out of the saved results so the first scan finds them as new; deploys the build; connects a throw-away administrator and a throw-away non-administrator websocket; and then checks the routes (Info, Catalog, Events), a scan (summary, review queue, the numbers equal the scan's own), a cancelled scan, apply and undo with real tag writes (the tags and locks are compared before and after), a cancelled apply, the master switch and one event switch, the automatic scan (3 new tracks send nothing, 30 send one summary), and an interrupted job found after a restart (announced once, not again after a second restart, numbers continue). At the end it deletes its users and copied files and restores everything from the backup, checking by hash. It exits 0 only when every check passed. Flags: `-Force` (skip the check that nobody used the server in the last 10 minutes), `-SkipAutoScan`.

To look at the alerts by hand, create a throw-away administrator, sign in as that user (`POST /Users/AuthenticateByName`), open `ws://localhost:8096/embywebsocket?api_key=<token>&deviceId=x` and read the text messages: the ones whose `MessageType` starts with `HallPass.` are the alerts. A socket opened with only the admin API key receives nothing.

## Pitfalls
- Emby caches a plugin's web page per plugin **version**: if you change the page without changing `AssemblyVersion`, a browser that already loaded it keeps the old page. Bump the version while developing.
- The test server may be shared: restarting it interrupts anyone playing.
- Music files are copyrighted: do not commit them (`samples/` is ignored) and do not paste lyrics anywhere.
- Online lookups call the real Deezer and iTunes. Keep test runs small and let the cache do its job.
- The test library also holds real tracks the owner added (artists such as Aerosmith, The Beatles, Barenaked Ladies, Alan Menken) with **embedded lyrics (`lyrics-XXX` tag) and `.lrc` files that differ from each other**: useful for conflict and false-positive checks. Never print their text.
- A scratch harness is the quickest way to see why a word matched: a console project that compiles `src/HallPass/Core/**/*.cs` and the embedded `default-wordlist.json` resource, calls the private `ProfanityScanner.Match` by reflection and prints token, term, weight and position. Dump embedded lyrics with `ffprobe -show_format` (Emby's copy is in `system\ffprobe.exe`).
- Emby's browser view cache: open a fresh browser tab to see a new page; an old tab keeps stacked stale views. There is a limit on open tabs in the Claude browser pane, so close old ones.
- Use a throw-away Emby user to test play counts (`POST /Users/{id}/PlayedItems/{itemId}` makes a count of 1) and delete it afterwards; `PlayCounts` caches for 60 s, so wait a minute after changing counts before checking order.
- After a scan-logic change the next "Scan now" rescans everything (the evidence version changed): wait for `Status.Job.State` to leave `Running` before reading results.
