# Security

## Reporting a problem
Please report security problems **privately**: on the repository page open the **Security** tab and choose **Report a vulnerability** (GitHub's private reporting). If that button is not there, open an ordinary issue that says only "I would like to report a security problem" and no details, and a private channel will be arranged.

Please do not post server addresses, API keys or logs from your own server in public issues.

## What the plugin does about security
- Every Hall Pass web address needs an Emby **administrator** login.
- Online lookups are **off by default** and ask for confirmation. When on, only the artist, title, ISRC or album barcode go to Deezer and iTunes; never file names, paths, lyrics or anything about the user or the server. Requests use HTTPS, a descriptive User-Agent and rate limits.
- Lyrics are read from Emby and never stored; only the result and the matched words are kept.
- Writing tags is refused while dry run is on, needs an explicit confirmation, and every change is journalled so it can be undone.
- Job ids are checked before they become file names (letters and digits only); text from files (titles, tags) is shown as text on the page, never as HTML.
- The release is built by GitHub Actions from this repository with signed build provenance (see the README for how to check a download).

## Known limits
- The settings, results and journal are plain files in Emby's `plugins/configurations` folder; anyone who can read that folder can read them (they hold no passwords or keys).
- Emby logs request URLs; Hall Pass puts no secrets in URLs.

## Supported versions
Only the latest release receives fixes.
