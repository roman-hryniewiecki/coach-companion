using System.Diagnostics;
using System.Text;
using CoachCompanion.Audio;
using CoachCompanion.Audio.WebRtc;
using CoachCompanion.Audio.Windows;
using CoachCompanion.Core;
using CoachCompanion.Speech;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.LibraryLoader;

var options = Args.Parse(args);
try
{
    return options.Command switch
    {
        "devices" => Devices(),
        "capture" => await CaptureAsync(options),
        "selftest" => await SelfTestAsync(options),
        "bench" => await BenchAsync(options),
        "live" => await LiveAsync(options),
        "aectest" => await AecTestAsync(options),
        "session" => await SessionAsync(options),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
    return 2;
}

static int Usage()
{
    Console.WriteLine("""
        CoachCompanion.Spike - Phase 0 / Phase 1 checks

          devices                                     list capture devices and open render sessions (who is playing)
          capture  --process Zoom | --pid N [--mic Logitech] [--aec [--ns]] [--seconds 20] [--out spike-out]
                                                      capture mic + target process loopback to mic.wav / loopback.wav
          selftest [--seconds 12] [--mic Logitech] [--aec [--ns]] [--out spike-out] [--model NAME|PATH] [--runtime vulkan|cpu]
                                                      play an English TTS sample from a helper process, capture it via
                                                      process loopback and the mic at the same time, then transcribe both
          bench    [--model NAME|PATH] [--wav spike-out/loopback.wav] [--runtime vulkan|cpu]
                                                      time Whisper on a WAV and report which runtime loaded
          live     --process Zoom | --pid N | --tts [--mic Logitech] [--aec [--ns]] [--seconds 600] [--model NAME|PATH]
                                                      full pipeline: VAD, segmentation, Whisper, live transcript + JSONL

          aectest  [--mic-wav spike-out/raw-mic.wav] [--far-wav spike-out/raw-loop.wav] [--offsets -100,0,100,...] [--ns] [--model NAME]
                                                      offline AEC sweep: feed the far end shifted by each offset, report residual and ERLE,
                                                      write the best result to spike-out/aec-best.wav and transcribe it

          session  --process Zoom | --tts [--mic Logitech] [--noaec] [--ns] [--seconds 30] [--model NAME]
                                                      the same SessionRunner the desktop app uses, driven from the console

          --aec    run the microphone through WebRTC echo cancellation with the loopback as far end (--ns adds noise suppression)
          --model  a file path or a short name such as small.en, large-v3-turbo, base (looked up in models/); default small.en
        """);
    return 1;
}

static int Devices()
{
    foreach (var d in AudioDevices.CaptureDevices())
    {
        Console.WriteLine($"  capture {(d.IsDefaultCommunications ? "*" : " ")} {d.Name}");
    }

    foreach (var s in AudioDevices.RenderSessions())
    {
        Console.WriteLine($"  render    {s.Device}  <-  {s.Process} (pid {s.Pid}) {s.State}");
    }

    return 0;
}

static async Task<int> CaptureAsync(Args a)
{
    int pid = a.Int("pid") ?? FindRootProcess(a.Get("process") ?? throw new ArgumentException("--process NAME or --pid N is required"));
    var sources = OpenSources(a, pid);
    using var cleanup = sources.Cleanup;
    await RunCaptureAsync(sources.Coach, sources.Client, a.Int("seconds") ?? 20, a.Get("out") ?? "spike-out");
    PrintAecStats(sources);
    return 0;
}

static async Task<int> SelfTestAsync(Args a)
{
    int seconds = a.Int("seconds") ?? 12;
    string outDir = a.Get("out") ?? "spike-out";
    string sample = TtsHelper.EnsureLoopSample(outDir);
    using var player = TtsHelper.StartPlayer(sample, seconds + 60);
    Console.WriteLine($"helper player pid {player.Id} loops the sample through your speakers; it is the loopback target");

    try
    {
        var sources = OpenSources(a, player.Id);
        using var cleanup = sources.Cleanup;
        var (micWav, loopWav) = await RunCaptureAsync(sources.Coach, sources.Client, seconds, outDir);
        PrintAecStats(sources);

        string model = ResolveModel(a.Get("model"));
        Console.WriteLine();
        Console.WriteLine($"expected text : {TtsHelper.Sentence}");
        Console.WriteLine("loopback.wav  :");
        await TranscribeAsync(model, loopWav, a.Get("runtime"));
        Console.WriteLine("mic.wav (shows how much speaker output the mic picks up):");
        await TranscribeAsync(model, micWav, a.Get("runtime"));
    }
    finally
    {
        TtsHelper.Kill(player);
    }

    return 0;
}

static async Task<int> BenchAsync(Args a)
{
    string wav = a.Get("wav") ?? Path.Combine(a.Get("out") ?? "spike-out", "loopback.wav");
    await TranscribeAsync(ResolveModel(a.Get("model")), wav, a.Get("runtime"));
    return 0;
}

static async Task<int> AecTestAsync(Args a)
{
    string micWav = a.Get("mic-wav") ?? Path.Combine("spike-out", "raw-mic.wav");
    string farWav = a.Get("far-wav") ?? Path.Combine("spike-out", "raw-loop.wav");
    float[] mic = ReadWavAsPipelineSamples(micWav);
    float[] far = ReadWavAsPipelineSamples(farWav);
    int[] offsetsMs = (a.Get("offsets") ?? "-200,-100,-60,-40,-20,0,20,40,60,100,140,200,300,400")
        .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
    int frameSamples = PipelineFormat.FrameSamples;
    int frames = Math.Min(mic.Length, far.Length) / frameSamples;
    int measureFrom = (int)(frames * 0.4) * frameSamples; // skip the convergence period
    double inputDb = Db(Rms(mic, measureFrom, frames * frameSamples));
    Console.WriteLine($"mic {mic.Length / 16000.0:0.0} s, far {far.Length / 16000.0:0.0} s, {frames} frames, mic rms after warm-up {inputDb:0.0} dBFS, far rms {Db(Rms(far, measureFrom, frames * frameSamples)):0.0} dBFS");
    Console.WriteLine("positive offset = far-end fed ahead of the mic frame it is paired with");

    float[]? best = null;
    double bestDb = double.MaxValue;
    int bestOffset = 0;
    var farFrame = new float[frameSamples];
    var nearFrame = new float[frameSamples];
    foreach (int offsetMs in offsetsMs)
    {
        int offsetFrames = offsetMs / PipelineFormat.FrameMilliseconds;
        using var aec = new WebRtcEchoCanceller(noiseSuppression: a.Get("ns") is not null);
        var output = new float[frames * frameSamples];
        for (int i = 0; i < frames; i++)
        {
            int fi = i + offsetFrames;
            Array.Clear(farFrame);
            if (fi >= 0 && fi < frames)
            {
                Array.Copy(far, fi * frameSamples, farFrame, 0, frameSamples);
            }

            Array.Copy(mic, i * frameSamples, nearFrame, 0, frameSamples);
            aec.FeedFarEnd(farFrame);
            aec.ProcessNearEnd(nearFrame);
            Array.Copy(nearFrame, 0, output, i * frameSamples, frameSamples);
        }

        double residualDb = Db(Rms(output, measureFrom, output.Length));
        Console.WriteLine($"  far offset {offsetMs,5} ms: residual {residualDb,6:0.0} dBFS, ERLE {inputDb - residualDb,5:0.0} dB, apm errors {aec.Errors}");
        if (residualDb < bestDb)
        {
            bestDb = residualDb;
            best = output;
            bestOffset = offsetMs;
        }
    }

    if (best is not null)
    {
        string outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(micWav))!, "aec-best.wav");
        using (var writer = new WaveFileWriter(outPath, new WaveFormat(PipelineFormat.SampleRate, 16, 1)))
        {
            writer.WriteSamples(best, 0, best.Length);
        }

        Console.WriteLine($"best offset {bestOffset} ms -> {outPath}");
        await TranscribeAsync(ResolveModel(a.Get("model")), outPath, a.Get("runtime"));
    }

    return 0;
}

static double Rms(float[] x, int from, int to)
{
    double ss = 0;
    int n = 0;
    for (int i = from; i < to && i < x.Length; i++)
    {
        ss += x[i] * x[i];
        n++;
    }

    return n == 0 ? 0 : Math.Sqrt(ss / n);
}

static double Db(double linear) => linear <= 1e-9 ? -100 : 20 * Math.Log10(linear);

static async Task<int> SessionAsync(Args a)
{
    int seconds = a.Int("seconds") ?? 30;
    string outDir = Path.GetFullPath(a.Get("out") ?? "spike-out");
    Process? player = null;
    if (a.Get("tts") is not null)
    {
        player = TtsHelper.StartPlayer(TtsHelper.EnsureLoopSample(outDir), seconds + 30);
        Console.WriteLine($"helper player pid {player.Id} stands in for the client");
    }

    try
    {
        await using var runner = new SessionRunner(
            new WindowsAudioPlatform(),
            noiseSuppression => new WebRtcEchoCanceller(noiseSuppression: noiseSuppression));
        runner.StatusChanged += s => Console.WriteLine($"status : {s}");
        runner.UtteranceReceived += u => Console.WriteLine($"[{u.Start:mm\\:ss\\.f}] {u.Speaker,-6} {u.Text}");

        string? micId = null;
        if (a.Get("mic") is { } micName)
        {
            micId = runner.Platform.CaptureDevices()
                .FirstOrDefault(d => d.Name.Contains(micName, StringComparison.OrdinalIgnoreCase))?.Id
                ?? throw new InvalidOperationException($"no capture device contains '{micName}'");
        }

        await runner.StartAsync(new SessionOptions
        {
            MicrophoneId = micId,
            TargetProcessId = player?.Id,
            TargetProcessName = player is null ? a.Get("process") ?? throw new ArgumentException("--process NAME or --tts is required") : null,
            ModelPath = ResolveModel(a.Get("model")),
            EchoCancellation = a.Get("noaec") is null,
            NoiseSuppression = a.Get("ns") is not null,
            SessionsDirectory = Path.Combine(outDir, "sessions"),
        });

        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Console.WriteLine($"stats  : {runner.Stats}");
        await runner.StopAsync();
    }
    finally
    {
        if (player is not null)
        {
            TtsHelper.Kill(player);
        }
    }

    return 0;
}

static async Task<int> LiveAsync(Args a)
{
    int seconds = a.Int("seconds") ?? 600;
    string outDir = a.Get("out") ?? "spike-out";
    Directory.CreateDirectory(outDir);

    Process? player = null;
    int pid;
    if (a.Get("tts") is not null)
    {
        player = TtsHelper.StartPlayer(TtsHelper.EnsureLoopSample(outDir), seconds + 30);
        pid = player.Id;
        Console.WriteLine($"helper player pid {pid} plays the TTS sample in a loop and stands in for the client");
    }
    else
    {
        pid = a.Int("pid") ?? FindRootProcess(a.Get("process") ?? throw new ArgumentException("--process NAME, --pid N or --tts is required"));
    }

    try
    {
        var sources = OpenSources(a, pid);
        using var cleanup = sources.Cleanup;
        using var stt = new WhisperEngine(ResolveModel(a.Get("model")), "en", preferGpu: a.Get("runtime") != "cpu");
        string jsonl = Path.Combine(outDir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        await using var pipeline = new TranscriptionPipeline(sources.Coach, sources.Client, stt, jsonl);
        Console.WriteLine($"coach  : {sources.Coach.Name}");
        Console.WriteLine($"client : {sources.Client.Name}");
        Console.WriteLine($"stt    : {stt.Description}");
        Console.WriteLine($"jsonl  : {jsonl}");
        Console.WriteLine();

        pipeline.Start();
        var printer = Task.Run(async () =>
        {
            await foreach (var u in pipeline.Utterances.ReadAllAsync())
            {
                Console.WriteLine($"[{u.Start:mm\\:ss\\.f}] {u.Speaker,-6} {u.Text}");
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("stopping...");
        await pipeline.StopAsync();
        await printer;
        var s = pipeline.Stats;
        Console.WriteLine($"segments: coach {s.CoachSegments}, client {s.ClientSegments}, discarded {s.SegmentsDiscarded}; transcribed {s.Transcribed}, empty {s.Empty}; frames dropped {s.FramesDropped}");
        PrintAecStats(sources);
    }
    finally
    {
        if (player is not null)
        {
            TtsHelper.Kill(player);
        }
    }

    return 0;
}

static MicCaptureSource OpenMic(Args a) =>
    a.Get("mic") is { } name ? MicCaptureSource.ByName(name) : MicCaptureSource.Default();

/// <summary>Mic + loopback, optionally wrapped in the echo-cancellation stage (--aec).</summary>
static Sources OpenSources(Args a, int pid)
{
    var mic = OpenMic(a);
    var loop = new ProcessLoopbackSource(pid);
    if (a.Get("aec") is null)
    {
        return new Sources(mic, loop, null, new Cleanup(loop, mic));
    }

    var canceller = new WebRtcEchoCanceller(noiseSuppression: a.Get("ns") is not null);
    var stage = new EchoCancellationStage(mic, loop, canceller);
    Console.WriteLine($"aec    : {canceller.Description}");
    return new Sources(stage.Coach, stage.Client, stage, new Cleanup(stage, loop, mic));
}

static void PrintAecStats(Sources sources)
{
    if (sources.Aec is { } aec)
    {
        Console.WriteLine($"aec      : {aec.FramesProcessed} mic frames processed, {aec.FarEndFrames} far-end frames fed");
    }
}

static async Task<(string MicWav, string LoopWav)> RunCaptureAsync(IAudioSource mic, IAudioSource loop, int seconds, string outDir)
{
    Directory.CreateDirectory(outDir);
    string micWav = Path.Combine(outDir, "mic.wav");
    string loopWav = Path.Combine(outDir, "loopback.wav");
    var micMeter = new Meter();
    var loopMeter = new Meter();

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    Console.WriteLine($"mic      : {mic.Name}");
    Console.WriteLine($"loopback : {loop.Name}");
    loop.Start();
    mic.Start();
    var micTask = WriteWavAsync(mic, micWav, micMeter);
    var loopTask = WriteWavAsync(loop, loopWav, loopMeter);

    var sw = Stopwatch.StartNew();
    while (!cts.IsCancellationRequested)
    {
        Console.Write($"\r{sw.Elapsed:mm\\:ss}  mic {micMeter.Bar()}   loop {loopMeter.Bar()}   ");
        try
        {
            await Task.Delay(250, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    Console.WriteLine();
    mic.Stop();
    loop.Stop();
    await Task.WhenAll(micTask, loopTask);
    Console.WriteLine($"mic      : {micMeter.Summary()}, dropped {mic.FramesDropped}  -> {micWav}");
    Console.WriteLine($"loopback : {loopMeter.Summary()}, dropped {loop.FramesDropped}  -> {loopWav}");
    return (micWav, loopWav);
}

static async Task WriteWavAsync(IAudioSource source, string path, Meter meter)
{
    await using var writer = new WaveFileWriter(path, new WaveFormat(PipelineFormat.SampleRate, 16, 1));
    await foreach (var frame in source.Frames.ReadAllAsync())
    {
        writer.WriteSamples(frame.Samples, 0, frame.Samples.Length);
        meter.Add(frame.Samples);
    }
}

static async Task TranscribeAsync(string modelPath, string wavPath, string? runtime)
{
    RuntimeOptions.RuntimeLibraryOrder = runtime?.ToLowerInvariant() switch
    {
        "cpu" => [RuntimeLibrary.Cpu],
        _ => [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu],
    };

    float[] samples = ReadWavAsPipelineSamples(wavPath);
    var sw = Stopwatch.StartNew();
    using var factory = WhisperFactory.FromPath(modelPath);
    using var processor = factory.CreateBuilder()
        .WithLanguage("en")
        .WithThreads(Math.Max(1, Environment.ProcessorCount / 2))
        .Build();
    var load = sw.Elapsed;
    sw.Restart();
    var text = new StringBuilder();
    await foreach (var segment in processor.ProcessAsync(samples))
    {
        text.Append(segment.Text);
    }

    Console.WriteLine($"  runtime {RuntimeOptions.LoadedLibrary}, model load {load.TotalMilliseconds:0} ms, transcribe {sw.ElapsedMilliseconds} ms for {samples.Length / (double)PipelineFormat.SampleRate:0.0} s of audio");
    Console.WriteLine($"  text: {text.ToString().Trim()}");
}

static float[] ReadWavAsPipelineSamples(string path)
{
    using var reader = new WaveFileReader(path);
    ISampleProvider provider = reader.ToSampleProvider();
    if (provider.WaveFormat.Channels == 2)
    {
        provider = new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f };
    }

    if (provider.WaveFormat.SampleRate != PipelineFormat.SampleRate)
    {
        provider = new WdlResamplingSampleProvider(provider, PipelineFormat.SampleRate);
    }

    var all = new List<float>();
    var buffer = new float[PipelineFormat.SampleRate];
    int read;
    while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
    {
        all.AddRange(buffer.Take(read));
    }

    return all.ToArray();
}

static string ResolveModel(string? modelArg)
{
    if (modelArg is not null && File.Exists(modelArg))
    {
        return modelArg;
    }

    // A short name (small.en, large-v3-turbo, base) or the default list, looked up in a models/
    // folder found by walking up from the executable and from the working directory.
    string[] names = modelArg is not null ? [modelArg] : ["small.en", "base"];
    foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            foreach (var name in names)
            {
                string candidate = Path.Combine(dir.FullName, "models", $"ggml-{name}.bin");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            dir = dir.Parent;
        }
    }

    throw new FileNotFoundException($"no Whisper model found for '{modelArg ?? "small.en"}'; pass --model PATH");
}

static int FindRootProcess(string name)
{
    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
    {
        name = name[..^4];
    }

    var processes = Process.GetProcessesByName(name);
    if (processes.Length == 0)
    {
        throw new InvalidOperationException($"no running process named {name}");
    }

    Process best = processes[0];
    DateTime bestStart = DateTime.MaxValue;
    foreach (var p in processes)
    {
        try
        {
            if (p.StartTime < bestStart)
            {
                bestStart = p.StartTime;
                best = p;
            }
        }
        catch
        {
            // access denied on StartTime for some processes
        }
    }

    Console.WriteLine($"target {best.ProcessName} pid {best.Id}: {processes.Length} matching process(es), capturing the tree under the oldest");
    return best.Id;
}

/// <summary>Creates an English speech sample with Windows TTS and plays it from a helper process.</summary>
static class TtsHelper
{
    public const string Sentence = "The client said she wants to change careers but is afraid of disappointing her family.";

    /// <summary>Returns a WAV with the sentence followed by 1.5 s of silence, generating it on first use.</summary>
    public static string EnsureLoopSample(string outDir)
    {
        Directory.CreateDirectory(outDir);
        string raw = Path.GetFullPath(Path.Combine(outDir, "tts.wav"));
        string loop = Path.GetFullPath(Path.Combine(outDir, "tts-loop.wav"));
        if (!File.Exists(raw))
        {
            Console.WriteLine("generating the TTS sample with Windows speech synthesis...");
            using var synth = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -Command \"Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.SetOutputToWaveFile('{raw}'); $s.Speak('{Sentence}'); $s.Dispose()\"")
            { UseShellExecute = false, CreateNoWindow = true })!;
            synth.WaitForExit();
            if (!File.Exists(raw))
            {
                throw new InvalidOperationException("TTS generation failed.");
            }
        }

        if (!File.Exists(loop))
        {
            using var reader = new WaveFileReader(raw);
            using var writer = new WaveFileWriter(loop, reader.WaveFormat);
            reader.CopyTo(writer);
            var silence = new byte[(int)(reader.WaveFormat.AverageBytesPerSecond * 1.5)];
            writer.Write(silence, 0, silence.Length);
        }

        return loop;
    }

    public static Process StartPlayer(string wavPath, int seconds) =>
        Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -Command \"(New-Object Media.SoundPlayer '{wavPath}').PlayLooping(); Start-Sleep {seconds}\"")
        { UseShellExecute = false, CreateNoWindow = true })!;

    public static void Kill(Process player)
    {
        try
        {
            player.Kill();
        }
        catch
        {
            // already gone
        }
    }
}

sealed class Meter
{
    private double _sumSquares;
    private long _samples;
    private long _frames;
    private float _peak;
    private float _recentRms;

    public void Add(float[] samples)
    {
        double ss = 0;
        foreach (var v in samples)
        {
            ss += v * v;
            float abs = Math.Abs(v);
            if (abs > _peak)
            {
                _peak = abs;
            }
        }

        _sumSquares += ss;
        _samples += samples.Length;
        _frames++;
        _recentRms = (float)Math.Sqrt(ss / samples.Length);
    }

    public string Bar()
    {
        double db = Db(_recentRms);
        int n = (int)Math.Clamp((db + 60) / 3, 0, 20);
        return $"[{new string('#', n)}{new string('.', 20 - n)}] {db,4:0} dB";
    }

    public string Summary() =>
        $"{_frames} frames ({_frames * PipelineFormat.FrameMilliseconds / 1000.0:0.0} s), rms {Db(Math.Sqrt(_sumSquares / Math.Max(1, _samples))):0} dBFS, peak {Db(_peak):0} dBFS";

    private static double Db(double linear) => linear <= 1e-9 ? -100 : 20 * Math.Log10(linear);
}

sealed class Args
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; private init; } = string.Empty;

    public static Args Parse(string[] args)
    {
        var result = new Args { Command = args.Length > 0 ? args[0] : string.Empty };
        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string key = args[i][2..];
            string value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            result._values[key] = value;
        }

        return result;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public int? Int(string key) => int.TryParse(Get(key), out var v) ? v : null;
}

sealed record Sources(IAudioSource Coach, IAudioSource Client, EchoCancellationStage? Aec, IDisposable Cleanup);

sealed class Cleanup(params IDisposable[] items) : IDisposable
{
    public void Dispose()
    {
        foreach (var item in items)
        {
            try
            {
                item.Dispose();
            }
            catch
            {
                // best effort
            }
        }
    }
}
