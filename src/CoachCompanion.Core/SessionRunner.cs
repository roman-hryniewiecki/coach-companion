using CoachCompanion.Audio;
using CoachCompanion.Speech;

namespace CoachCompanion.Core;

public sealed record SessionOptions
{
    /// <summary>Capture device id from <see cref="IAudioPlatform.CaptureDevices"/>; <c>null</c> = default communications device.</summary>
    public string? MicrophoneId { get; init; }

    /// <summary>Root process of the meeting app to capture, by name (the oldest process with that name wins).</summary>
    public string? TargetProcessName { get; init; }

    /// <summary>Explicit process id; takes precedence over <see cref="TargetProcessName"/>.</summary>
    public int? TargetProcessId { get; init; }

    public required string ModelPath { get; init; }

    public string Language { get; init; } = "en";

    public bool PreferGpu { get; init; } = true;

    public bool EchoCancellation { get; init; } = true;

    public bool NoiseSuppression { get; init; }

    public required string SessionsDirectory { get; init; }
}

/// <summary>
/// Owns one listening session: resolves the target process, opens the microphone and the
/// application loopback through <see cref="IAudioPlatform"/>, optionally wraps them in echo
/// cancellation, loads the speech engine and runs the <see cref="TranscriptionPipeline"/>.
/// Events are raised on background threads.
/// </summary>
public sealed class SessionRunner : IAsyncDisposable
{
    private readonly IAudioPlatform _platform;
    private readonly Func<bool, IEchoCanceller> _echoCancellerFactory;
    private TranscriptionPipeline? _pipeline;
    private ISpeechToText? _stt;
    private EchoCancellationStage? _echoStage;
    private IAudioSource? _mic;
    private IAudioSource? _loopback;
    private Task? _reader;

    /// <param name="echoCancellerFactory">Creates a canceller; the argument is "noise suppression on".</param>
    public SessionRunner(IAudioPlatform platform, Func<bool, IEchoCanceller> echoCancellerFactory)
    {
        _platform = platform;
        _echoCancellerFactory = echoCancellerFactory;
    }

    public event Action<Utterance>? UtteranceReceived;

    public event Action<string>? StatusChanged;

    public IAudioPlatform Platform => _platform;

    public bool IsRunning => _pipeline is not null;

    public string? TranscriptPath { get; private set; }

    public string? EngineDescription => _stt?.Description;

    public PipelineStats? Stats => _pipeline?.Stats;

    public EchoCancellationStage? EchoStage => _echoStage;

    public async Task StartAsync(SessionOptions options, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A session is already running.");
        }

        System.Diagnostics.Process target;
        if (options.TargetProcessId is { } pid)
        {
            target = System.Diagnostics.Process.GetProcessById(pid);
        }
        else if (!string.IsNullOrWhiteSpace(options.TargetProcessName))
        {
            target = ProcessLocator.FindRoot(options.TargetProcessName)
                ?? throw new InvalidOperationException($"{options.TargetProcessName} is not running. Start the meeting app first.");
        }
        else
        {
            throw new ArgumentException("Either TargetProcessName or TargetProcessId is required.");
        }

        Report($"Loading {Path.GetFileName(options.ModelPath)}...");
        ISpeechToText stt = await Task.Run(
            () => (ISpeechToText)new WhisperEngine(options.ModelPath, options.Language, options.PreferGpu),
            cancellationToken).ConfigureAwait(false);

        IAudioSource? mic = null;
        IAudioSource? loopback = null;
        EchoCancellationStage? echoStage = null;
        try
        {
            Report("Opening audio...");
            mic = _platform.OpenMicrophone(options.MicrophoneId);
            loopback = _platform.OpenApplicationLoopback(target.Id);
            IAudioSource coach = mic;
            IAudioSource client = loopback;
            if (options.EchoCancellation)
            {
                echoStage = new EchoCancellationStage(mic, loopback, _echoCancellerFactory(options.NoiseSuppression));
                coach = echoStage.Coach;
                client = echoStage.Client;
            }

            Directory.CreateDirectory(options.SessionsDirectory);
            TranscriptPath = Path.Combine(options.SessionsDirectory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
            var pipeline = new TranscriptionPipeline(coach, client, stt, TranscriptPath);
            pipeline.Start();

            _stt = stt;
            _mic = mic;
            _loopback = loopback;
            _echoStage = echoStage;
            _pipeline = pipeline;
            _reader = Task.Run(async () =>
            {
                await foreach (var utterance in pipeline.Utterances.ReadAllAsync().ConfigureAwait(false))
                {
                    UtteranceReceived?.Invoke(utterance);
                }
            });

            Report($"Listening to {target.ProcessName} (pid {target.Id}) with {stt.Description}");
        }
        catch
        {
            echoStage?.Dispose();
            mic?.Dispose();
            loopback?.Dispose();
            stt.Dispose();
            TranscriptPath = null;
            throw;
        }
    }

    public async Task StopAsync()
    {
        var pipeline = _pipeline;
        if (pipeline is null)
        {
            return;
        }

        Report("Stopping...");
        await pipeline.StopAsync().ConfigureAwait(false);
        if (_reader is not null)
        {
            await _reader.ConfigureAwait(false);
        }

        await pipeline.DisposeAsync().ConfigureAwait(false);
        _echoStage?.Dispose();
        _mic?.Dispose();
        _loopback?.Dispose();
        _stt?.Dispose();
        _pipeline = null;
        _echoStage = null;
        _mic = null;
        _loopback = null;
        _stt = null;
        _reader = null;
        Report($"Stopped. Transcript: {TranscriptPath}");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void Report(string status) => StatusChanged?.Invoke(status);
}
