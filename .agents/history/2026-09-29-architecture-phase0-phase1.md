# Architecture rethink, Phase 0 spike, Phase 1 pipeline

- Date: 2026-09-29
- Agent: Claude (Claude Code)
- Commits: none at the time (the folder was not yet a git repository); the work is part of the
  first import commit of 2026-09-30

Backfilled on 2026-09-30 from `docs/ARCHITECTURE.md` and the agent's notes of that session,
so that the history starts at the beginning. The measurements below are quoted from
`docs/ARCHITECTURE.md` section 6; they were not re-run for this entry.

## Done

- Diagnosed why the old WPF prototype (`CouchCompanion/`) fails at runtime (raw float samples
  passed to Whisper's WAV-stream overload, no mic capture, device-wide loopback, 10 Whisper
  calls per second). Details in `docs/ARCHITECTURE.md` section 2.
- Wrote `docs/ARCHITECTURE.md`: WASAPI process loopback + shared-mode mic, two channels as the
  coach/client split, local Whisper behind `ISpeechToText`, Avalonia UI, LLM analysis loop.
- Created `CoachCompanion.sln` with `src/CoachCompanion.{Audio,Audio.Windows,Audio.WebRtc,Speech,Core,App}`
  and `tools/CoachCompanion.Spike`.
- Phase 0: process loopback and mic capture, Whisper timing on the GPU (Vulkan runtime).
- Phase 1: VAD + utterance segmentation, Whisper transcription, JSONL transcript, WebRTC AEC3
  echo cancellation (`SoundFlow.Extensions.WebRtc.Apm`), `Core.SessionRunner`, Avalonia 12
  window (target / mic / model pickers, AEC toggle, start / stop, two-colour transcript).

## Verified (on the owner's machine, that day)

- Process loopback of a helper process: works, 0 frames dropped. Mic capture in parallel: works.
- Whisper `small.en`: 11.9 s of audio in 0.8 s. `large-v3-turbo`: 2.4 s. Both on a GTX 1650 Ti.
- AEC3 with far end paired by arrival order: 4 dB reduction. With the loopback fed at least
  40 ms ahead of the mic: 35 dB. Implemented as a 100 ms mic delay line in `EchoCancellationStage`.
- `live` pipeline with AEC: 0 coach segments caused by echo, 5 of 5 client sentences correct.
- The Avalonia window renders and lists devices and models.

## Not verified / known gaps

- A real Zoom call (tests used a helper process playing TTS audio).
- Double talk (coach speaking over the client); needs a person at the microphone.
- Silero VAD is planned, not in place. No overlay, AppBar docking, meeting-window tracking,
  LLM analysis or Linux platform yet.

## Decisions (owner)

- No driver, no virtual cable.
- Sessions are English only. Windows now, Fedora 44 later.
- Camera microphone without headphones, hence in-app echo cancellation.
- `small.en` is the default model, `large-v3-turbo` selectable.

## Next

1. Owner checks a real Zoom call: `spike live --process Zoom --aec --mic Logitech` or the desktop app.
2. Phase 2: AppBar panel, overlay, meeting-window tracking, exclude-from-capture.
3. Phase 3: framework YAML, analysis loop, stage display, question suggestions.
