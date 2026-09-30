# Coach Companion – notes for agents

Several agents (and the owner) work on this repository in turns. Read this file, then
`docs/ARCHITECTURE.md`, then the newest entries in `.agents/history/` before changing anything.

## What this is

A desktop tool that listens to a live coaching session held in Zoom, transcribes both sides
(coach = microphone, client = the meeting app's audio) and will show live coaching guidance.
Windows first, Fedora later. Design, decisions and measured results: `docs/ARCHITECTURE.md`.

## Layout

| Path | What |
|---|---|
| `src/` | the product: `Audio`, `Audio.Windows`, `Audio.WebRtc`, `Speech`, `Core`, `App` (Avalonia) |
| `tools/CoachCompanion.Spike` | console harness: `devices`, `capture`, `selftest`, `bench`, `aectest`, `live`, `session` |
| `CouchCompanion/` | the old WPF prototype; broken at runtime, kept for reference only, do not extend |
| `models/` | Whisper ggml models (`ggml-small.en.bin` default, `ggml-large-v3-turbo.bin`); **not in git** |
| `spike-out/` | recordings and transcripts from local runs; **not in git** (may hold confidential session audio) |
| `.agents/history/` | one summary per working session, see below |

Build: `dotnet build CoachCompanion.sln` (.NET 8 target, Windows). A fresh clone needs the
model files placed in `models/` before anything that transcribes will run; ask the owner before
downloading them (0.5 to 1.6 GB each).

## Rules that are already decided

- No audio driver and no virtual cable. Capture is WASAPI process loopback plus shared-mode mic.
- Platform-specific code stays behind interfaces (`IAudioPlatform`, `IAudioSource`, later
  `IWindowPlatform`) so the Linux port stays possible.
- `EchoCancellationStage` delays the microphone by 100 ms so the loopback reference reaches the
  canceller first. Do not remove that lead; the measurements are in `docs/ARCHITECTURE.md` section 6.
- Sessions are in English; `WhisperEngine` defaults to language `en`, keep it that way.
- Never commit recordings, transcripts, model files, API keys or anything under `spike-out/`.
- Claims in docs and history entries must be verified ones. Say what was measured or run, and
  list separately what was not checked.

## Session history (required)

At the end of every working session, add one file to `.agents/history/` and commit it together
with the work. Format and naming are described in `.agents/history/README.md`.
