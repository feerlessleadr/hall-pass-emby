# Hall Pass (for Emby)

Emby Server plugin that scans music tracks, decides whether each one is explicit (embedded flags, lyrics, online catalogs) and tags it, so a smart playlist can exclude the tag.

Status: just started (design only). See [docs/DESIGN.md](docs/DESIGN.md) for the plan and [PROGRESS.md](PROGRESS.md) for what is done.

Companion plugin: Tag Team (the bulk tag editor, https://github.com/feerlessleadr/tag-team-emby).

Part of the owner's Emby tooling next to [Hall Monitor](https://github.com/feerlessleadr/hall-monitor-emby) and the Embidextrous phone app.

## Smart list recipes (checked against the SmartLists plugin)
Hall Pass puts exactly one of four tags on each decided track (the names are settings): `explicit`, `clean`, `likely-clean`, `explicit-review`. In a smart list on **Tags**:

| Playlist | Rule | Notes |
|----------|------|-------|
| Strict (only confirmed clean) | `Tags` **Equal** `clean` | A track is `clean` only with strong evidence (an embedded or online "clean" flag, or a manual decision). |
| Balanced (recommended) | `Tags` **Contains** `likely-clean` (plus a second rule or list for `clean`) | Tracks whose lyrics have no scoring words and where nothing explicit turned up. |
| Conservative (leave out anything doubtful) | `Tags` **NotContains** `explicit` | "Contains" is a substring match, so this also leaves out `explicit-review`. |
| Relaxed (leave out only what is certain) | `Tags` **NotEqual** `explicit` | The whole tag only, so `explicit-review` tracks stay in. |

Run a full scan, preview and apply before trusting a playlist: a track with no tag at all passes any "not" rule.
