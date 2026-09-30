# Coach Companion – architecture rethink

Date: 2026-09-29. Status: proposal, decision gate after the Phase 0 spike.

## 1. Goal

A Windows desktop tool that listens to a live coaching session held in Zoom (later Teams,
Google Meet, Discord), transcribes **both sides** in real time with a reliable
"coach vs. client" split, and shows live guidance (current phase in the coaching arc /
FLIP IT, framework stages, powerful questions) in an always-on-top transparent overlay
and in a panel docked above the meeting window. Runs locally where possible; transcripts
are the coach's confidential session material.

Facts fixed on 2026-09-29: sessions are in **English only**; Windows first, **Fedora 44
later** (so platform code stays behind interfaces and the UI is cross-platform); the mic is
the one built into a Logitech Brio 4K, used **without headphones**, so the mic channel will
contain the client's voice from the speakers and needs echo cancellation.

## 2. What exists today and why it does not work

`CouchCompanion/` is a .NET 8 WPF app (~600 lines): screen/window picker, NAudio
`WasapiLoopbackCapture`, Whisper.net with the `base` model, and a one-shot docking routine.
It **builds with 0 errors**; every failure is at runtime.

Confirmed root causes (reproduced on this machine on 2026-09-29):

1. **Whisper is fed the wrong thing.** `WhisperService.ProcessAudioAsync` writes raw float32
   samples into a `MemoryStream` and passes it to `ProcessAsync(Stream)`, which expects a
   WAV *file*. Every chunk throws `CorruptedWaveException: Invalid wave file RIFF header.`
   The call sits in an `async void` handler on NAudio's capture thread, so the exception is
   unobserved and takes the process down. `app_log.txt` ends right after the second chunk.
   The `ProcessAsync(float[])` overload works (verified).
2. **Even with the right overload the pipeline cannot keep up.** 100 ms chunks are padded
   into 1 s buffers that are 90 % zeros; whisper.cpp pads every call to a 30 s window, so
   `base` on CPU costs ~2.7 s per call here. Ten calls per second, concurrently, on one
   non-thread-safe `WhisperProcessor`.
3. **Only one direction is captured.** There is no microphone capture at all; "what I am
   saying" never enters the system.
4. **Loopback is device-wide, not per app.** The "process" selection only picks the render
   device that Zoom has a session on; everything played on that device is captured.
5. A new `MediaFoundationResampler` is created per 100 ms chunk (boundary artifacts,
   dropped samples), language is auto-detected per 1 s chunk, and docking is a one-shot
   `SetWindowPos` side-by-side that does not follow the meeting window.

Verdict: keep WPF + MVVM as the shell; **rewrite the audio/STT pipeline** rather than patch
it. It is ~300 lines and every part of it is wrong in a different way.

## 3. Core decision: no driver, no virtual cable

The instinct "something like OBS or a driver between the app and the OS" is right about the
*layer* and wrong about the *mechanism*. That layer already exists in Windows:

| Need | Windows API | Driver? |
|---|---|---|
| What others say (Zoom's output only) | WASAPI **process loopback**: `ActivateAudioInterfaceAsync` on `VAD\Process_Loopback` with `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, target PID, `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE` | No |
| What I say (mic) | WASAPI shared-mode capture on the mic endpoint; Zoom and we read the same mic concurrently | No |
| Fallback (older Windows, browsers) | WASAPI device loopback (what the old app does) | No |

This is exactly what OBS's "Application Audio Capture" source uses. It taps the audio of one
process tree before it reaches the device, does not change Zoom's device selection, needs no
install, no signing, no admin, and works for any application. Requires Windows 11 or a late
Windows 10 build; this machine (build 26200) qualifies. NAudio 2.2.1 already ships the
activation types (`AudioClientActivationParams`, `AudioClientProcessLoopbackParams`,
`ProcessLoopbackMode`) and an `ActivateAudioInterfaceAsync` binding; we add a completion
handler and a `GetBuffer`/`ReleaseBuffer` capture loop (~100 lines). If the binding turns out
to be internal, CsWin32 generates the same call.

Why not the alternatives:

- **Custom kernel driver / APO:** WDM or APO development plus attestation signing, for zero
  functional gain over the API above. Only justified if we needed to *inject* audio into the
  call. We do not.
- **Virtual cable (VB-Cable, VoiceMeeter):** third-party driver install, forces the user to
  re-route Zoom's speaker and mic through the cable and back to real hardware, breaks when
  Zoom auto-switches devices, adds latency, and still is not per-app.
- **Zoom Meeting SDK / RTMS:** gives per-participant audio, but needs a Zoom Marketplace app,
  host consent and a server, and is Zoom-only. Keep as a future optional source for
  multi-party diarization.
- **Scraping Zoom captions or Windows Live Captions via UI Automation:** brittle, no speaker
  split, no timestamps.

**Two channels give speaker attribution for free.** Mic = coach, loopback = client. For 1:1
coaching no diarization model is needed. Multi-party sessions get diarization later.

Echo: the Brio mic without headphones also hears the client, which would duplicate text
under "me". The loopback channel is exactly the far-end reference an acoustic echo canceller
needs, so the plan is an in-app AEC (Speex or WebRTC AEC3, cross-platform, works on Fedora
too) fed with mic + loopback, aligned by capture timestamps. Fallbacks: Windows 11 platform
AEC on a Communications-category capture stream (`IAcousticEchoCancellationControl`, driver
dependent), and cheap cross-channel suppression (drop mic segments that overlap loud loopback
activity with near-identical text). Phase 0 records both channels so the leakage can be
measured before choosing.

## 4. Target architecture

```
Mic endpoint ──WASAPI shared capture──┐
                                      ├─ resample 16 kHz mono ─ VAD (Silero) ─ segmenter ─ STT ─► Utterance
Zoom.exe tree ─WASAPI process loopback┘        (one chain per channel, channel = speaker)         │
                                                                                                  ▼
                                                  Transcript store (in-memory + JSONL per session on disk)
                                                                                                  │
                                            Analysis loop (client turn end / every 30–60 s) ─ LLM ─► CoachingState
                                                                                                  │
                                                        UI: transparent overlay + docked guidance panel
```

Projects (single solution, .NET 8, C#):

| Project | Responsibility | Key pieces |
|---|---|---|
| `CoachCompanion.Audio` (net8.0) | platform-neutral audio: contracts, resample, VAD, segmentation, WAV | `IAudioSource` (20 ms frames, 16 kHz mono float), one `WdlResampler` per channel, Silero VAD via ONNX Runtime, AEC |
| `CoachCompanion.Audio.Windows` (net8.0-windows) | WASAPI sources | `MicCaptureSource` (NAudio `WasapiCapture`), `ProcessLoopbackSource` (NAudio activation types + completion handler + own capture loop) |
| `CoachCompanion.Audio.Linux` (later) | PipeWire sources for Fedora | per-app stream capture and mic, same `IAudioSource` |
| `CoachCompanion.Speech` | `ISpeechToText` | `WhisperEngine` (Whisper.net, GPU runtime, one processor per channel, VAD-delimited 1–15 s segments, language fixed to `en`, `no_context`), `CloudStreamingEngine` (Azure Speech or Deepgram, interim results) |
| `CoachCompanion.Core` | transcript model, session recording, frameworks, analysis | `Utterance {Speaker, Text, T0, T1, IsFinal}`, `frameworks/*.yaml` (stages, markers, question bank), analysis orchestrator, Anthropic C# SDK client |
| `CoachCompanion.App` | **Avalonia** UI (Windows + Linux) with per-platform window services | control window, overlay window, guidance panel, meeting-window tracker, app profiles (Zoom, Teams, Discord, browser); Win32 AppBar / display affinity / WinEvent hooks behind an `IWindowPlatform` interface, a Wayland/X11 implementation later |

Threading rules: audio callbacks only enqueue into bounded `Channel<T>`; dedicated worker
tasks per stage; UI touched only via `Dispatcher`; no `async void` anywhere.

### 4.1 Speech-to-text

| | Whisper.net local (GPU) | Azure Speech streaming | Deepgram streaming |
|---|---|---|---|
| Latency | 1–3 s after utterance end | interim results < 0.5 s | interim results < 0.5 s |
| Polish | good with `large-v3-turbo`, poor with `base` | very good | good |
| Privacy | fully local | cloud | cloud |
| Cost | none | ~1 USD / h / channel | ~0.5 USD / h / channel |
| Effort | medium (VAD + segmentation) | low (push stream) | low |

Default: local Whisper behind `ISpeechToText`, cloud engine as a switchable option. GPU here is
a GTX 1650 Ti (4 GB): `large-v3-turbo` fits; try `Whisper.net.Runtime.Vulkan` first (no CUDA
toolkit needed), `Whisper.net.Runtime.Cuda` if faster. Phase 0 measures real numbers. Phase
detection is a slow loop, so segment latency of a couple of seconds is acceptable; a "live
captions" feel needs the cloud engine.

### 4.2 Windows: docking, overlay, meeting window

- **Docked guidance panel:** register the panel as an **AppBar** at the top edge
  (`SHAppBarMessage` with `ABM_NEW` / `ABM_QUERYPOS` / `ABM_SETPOS`). Windows shrinks the
  work area, so a maximized or snapped Zoom fills the space *below* the panel for free, for
  any app, and survives Zoom's own resizing. Per-monitor.
- **Follow mode (alternative):** find the meeting window (process name + largest visible
  top-level window; Zoom's meeting window class is commonly `ZPContentViewWndClass`, verify
  with `EnumWindows`), place it with `SetWindowPos`, track moves with
  `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE)`, re-dock on `EVENT_OBJECT_DESTROY`.
- **Overlay:** WPF window with `AllowsTransparency`, `Topmost`, extended styles
  `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` (click-through,
  never steals focus, not in Alt-Tab), hotkey to toggle click-through, DPI-aware.
- **Private during screen share:** `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` on
  the overlay and the panel, so guidance never appears in a Zoom share or recording.
- Full-screen Zoom ignores AppBars; the overlay still works because it is topmost.

### 4.3 Coaching intelligence

- Input per call: framework definition (cached prefix), previous `CoachingState`, rolling
  speaker-labelled transcript window (last ~10 min).
- Output (structured JSON): `framework`, `current_stage`, `confidence`, `evidence` (utterance
  ids), `stage_progress`, `suggested_questions` (max 3, open, stage-appropriate), `flags`
  (closed question, advice-giving, leading, interrupted client).
- Trigger: on client turn end, and at least every 30–60 s; debounced; only the transcript
  delta plus previous state is new input, so the framework and system prompt stay cached.
- Model: `claude-opus-5-5` via the official Anthropic C# SDK, adaptive thinking, low or medium
  effort for latency, structured outputs (`output_config.format`). Roughly 120 calls per hour
  at ~3 k input / 300 output tokens lands in the 1–2 USD per session range before cache
  savings; a cheaper model is a later, deliberate choice.
- Frameworks are data, not code: `frameworks/coaching-arc.yaml`, `frameworks/flip-it.yaml`
  with stages, definitions, transition markers and example questions; more can be added
  without a rebuild.
- Sessions: transcript + state JSONL per session, local only, delete from the UI; consent to
  transcription is asked of the client at session start.

## 5. Delivery plan

| Phase | Deliverable | Gate |
|---|---|---|
| 0 – Spike (done 2026-09-29) | console app: mic + process loopback captured simultaneously into two WAVs with level meters; GPU Whisper timing | process loopback works on this machine; STT latency acceptable |
| 1 – Pipeline (done 2026-09-29, AEC included) | VAD, segmentation, Whisper GPU, transcript JSONL, WebRTC echo cancellation, Avalonia transcript window with two colours (coach / client) | a full session transcribes end to end without falling behind |
| 2 – Windows | AppBar panel, overlay, meeting-window tracking, exclude-from-capture, app profiles | usable during a real Zoom call |
| 3 – Intelligence | framework YAML, analysis loop, stage display, question suggestions, session export | phase detection judged useful on recorded sessions |
| 4 – Breadth | Teams / Discord / browser profiles, cloud STT option, AEC, multi-party diarization | |

Repo hygiene alongside Phase 0: `git init` with a `.gitignore` (bin, obj, models); one copy of
the 148 MB model outside the source tree (it currently sits both in `models/` and inside
`CouchCompanion/Models/` next to source files); rename `CouchCompanion` to `CoachCompanion`.

## 6. Results so far (2026-09-29)

Phases 0 and 1 are done and verified on this machine with the spike
(`tools/CoachCompanion.Spike`: `devices`, `capture`, `selftest`, `bench`, `aectest`, `live`,
`session`). The camera mic Windows reports is "Microphone (Logitech Webcam C920-C)"; it is
the default communications device.

| Check | Result |
|---|---|
| Process loopback of a helper process (WASAPI, no driver) | works, 0 frames dropped |
| Mic capture in parallel (shared mode) | works |
| Whisper `small.en` on the GTX 1650 Ti via Vulkan | 11.9 s of audio in 0.8 s, sentence perfect |
| Whisper `large-v3-turbo` via Vulkan | 11.9 s of audio in 2.4 s, sentence perfect; selectable, `small.en` is the default |
| Speaker bleed into the camera mic, no AEC | mic at -34 dBFS transcribed the whole sentence |
| WebRTC AEC3, far end paired with the mic by arrival order | 4 dB reduction, echo still transcribed |
| WebRTC AEC3, far end fed >= 40 ms ahead of the mic | 35 dB reduction, residual -71 dBFS, Whisper hears blank audio |
| End-to-end `live` pipeline with AEC (100 ms mic delay line) | 0 coach segments from echo, 5 of 5 client sentences correct |
| Avalonia 12 window (target, mic, model, AEC toggles, transcript) | renders, devices and models detected |

The AEC finding matters for the design: the loopback reference reaches the pipeline slightly
*after* the echo it explains, and AEC3 cannot handle a negative delay. The stage therefore feeds
loopback frames to the canceller as they arrive and delays microphone frames by 100 ms before
cancellation, which sits in the middle of the plateau measured by `aectest` (40 to 400 ms).

Not yet verified: a real Zoom call (`spike live --process Zoom --aec --mic Logitech`, or the
desktop app), and coach speech overlapping client speech (double talk), which needs a person
at the microphone.

## 7. Risks and open questions

- Process loopback needs a PID: watch for Zoom starting after the app and auto-attach.
- Browsers (Meet) capture the whole browser tree, so other tabs' audio comes along; accept or
  fall back to device loopback.
- New Teams renders audio in WebView2 child processes; the process-tree include mode covers it.
- Bluetooth headsets in HFP mode lower mic quality; still fine for STT.
- Two Whisper processors on a 4 GB GPU: `large-v3-turbo` fits; otherwise serialize segments
  through one queue.
- Windows privacy setting "Let desktop apps access your microphone" must be on.
- Session language (Polish vs. English) decides the Whisper model and fixed language setting.
