# PROGRESS: Hall Pass

## 2026-10-12: started
- Repo created by the owner; design and planning decisions copied into `docs/`. Names and GUID fixed (see CLAUDE.md).
- The design document uses working names (EmbyExplicitTagger, EmbyBulkTagEditor, routes like /ExplicitTagger and /BulkTags): the fixed names in CLAUDE.md replace them everywhere.
- Owner's choices: a full web page in the Emby dashboard in addition to a phone/app UI (the Embidextrous app gets a tab later).

## Next
- Phase 0 (see docs/DESIGN.md): decompile the Emby DLLs, write docs/API-NOTES.md, hello-world plugin that loads and shows a menu entry.

## Open questions
- How to share the tag-writing code between this plugin and its companion (separate repos): copy kept in sync, or a git subtree. Decide when the first shared code exists.
