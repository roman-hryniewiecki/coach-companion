# Coaching arc and powerful questions transcribed

- Date: 2026-10-15
- Agent: Claude Opus 5.5 (Claude Code, desktop app)
- Commits: the commit that adds this file

## Done

- The owner supplied three slides (coaching arc diagram, five stage cards, "Powerful
  questions") and decided that the coaching arc is the first framework to implement, with the
  powerful questions "always on" across all stages.
- `framework/coaching-arc/README.md`: the five stages with stable ids (`align`, `goal`,
  `explore`, `action`, `growth`), both stage names (the arc slide and the card slide differ),
  card bullets, icons.
- `framework/tools/powerful-questions.md`: all 47 questions in slide order, each tagged
  exploration (E), action-oriented (A) or both, with the icon counts from the slide.
- `AGENTS.md` layout table and `docs/ARCHITECTURE.md` section 4.3 point to `framework/`.
- Earlier in the session: told the owner how to launch the app (`dotnet run --project
  src/CoachCompanion.App`) and the spike `live` command for a Zoom test. No results reported yet.

## Verified

- Transcription read back against the slide images by the agent. Nothing was built or run;
  no code changed.

## Not verified / known gaps

- Icon meaning (light bulb = exploration, blue icon = action-oriented) is inferred. The
  slide's legend is cut off. Owner to confirm.
- The last bullet of the "Exploration for learning" card sits on the card's edge; more text
  may have been cut off.
- The slide images are not in the repository. The questions slide carries a training
  provider's logo, so check whether the transcribed material may be published in this public
  repository before pushing.

## Decisions

- Owner: coaching arc first; powerful questions always on.
- Agent: the questions file sits at `framework/tools/` rather than the repo's `tools/`, which
  holds code (the spike). File name spelled `powerful-questions.md`.
- Agent: Markdown is the source; the Phase 3 YAML (`frameworks/coaching-arc.yaml` in the
  architecture doc) is to be derived from it.

## Next

1. Owner confirms the icon legend and whether this material can be public.
2. Real Zoom test (still open from 2026-09-29).
3. Phase 3 groundwork: derive `coaching-arc.yaml` (stages, transition markers, question bank
   with E/A tags) from these files.
