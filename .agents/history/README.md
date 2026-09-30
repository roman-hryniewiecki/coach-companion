# Session history

One Markdown file per working session, written by whoever did the work (agent or human) and
committed together with that work. The next agent reads the newest entries to pick up where
the last one stopped, so write for a reader who has not seen your session.

## File name

`YYYY-MM-DD-short-slug.md`, date = the day the session ended, for example
`2026-09-30-repo-setup.md`. Two sessions on the same day get different slugs. Never edit
someone else's entry; add a new one that corrects it.

## Contents

```markdown
# <title>

- Date: YYYY-MM-DD
- Agent: <model or person, and the tool, e.g. "Claude Fable 5.1 (Claude Code)">
- Commits: <hashes or range, if any>

## Done
What changed, in a few bullets. Name files and commands.

## Verified
What was actually run or measured, with the result.

## Not verified / known gaps
What was written but not exercised, and anything that is known to be broken.

## Decisions
Decisions taken in the session and who took them (owner or agent), with the reason.

## Next
The concrete next steps, most important first.
```

Keep it short, one screen is the target. Longer material (designs, measurements) belongs in
`docs/` with a link from the entry. No secrets, no client names, no transcript content.
