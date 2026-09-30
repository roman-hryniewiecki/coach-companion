# Repository setup and first push

- Date: 2026-09-30
- Agent: Claude Fable 5.1 (Claude Code)
- Commits: the import commit on `main` that contains this file (on top of GitHub's "Initial commit" with the LICENSE)

## Done

- Turned the local folder into a git repository and pushed it to
  https://github.com/roman-hryniewiecki/coach-companion (`main`, based on the existing
  commit that holds the Apache 2.0 `LICENSE`).
- Added `AGENTS.md` (orientation and decided rules for every agent), `CLAUDE.md` (imports
  `AGENTS.md`), `.agents/history/README.md` (format of these entries) and a backfilled entry
  for the 2026-09-29 session.
- No source code was changed.

## Verified

- `dotnet build CoachCompanion.sln`: 0 warnings, 0 errors (SDK 9.0.318, projects target .NET 8).
- The commit holds source and docs only: nothing under `bin/`, `obj/`, `models/`, `spike-out/`,
  no `app_log.txt`, largest file well under 500 KB. Searched the tracked files for keys, tokens
  and passwords: none.

## Not verified / known gaps

- Nothing was run besides the build. The open checks from 2026-09-29 (real Zoom call, double
  talk) are still open.
- The repository is public. `spike-out/` on the owner's machine holds test recordings and is
  ignored; keep it that way.
- A fresh clone has no Whisper models; they must be placed in `models/` by hand.
- The old prototype folder is still named `CouchCompanion/` and is still in the tree (reference only).
- There is no `README.md` for human visitors yet.

## Decisions (owner)

- Work continues in the GitHub repository.
- Every session ends with a summary in `.agents/history/`, because several agents work on the project.

## Next

1. Owner: real Zoom call check (`spike live --process Zoom --aec --mic Logitech` or the desktop app).
2. Phase 2 from `docs/ARCHITECTURE.md`: AppBar panel, overlay, meeting-window tracking, exclude-from-capture.
3. Phase 3: framework YAML, analysis loop, question suggestions.
