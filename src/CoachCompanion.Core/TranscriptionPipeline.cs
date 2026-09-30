using System.Threading.Channels;
using CoachCompanion.Audio;
using CoachCompanion.Speech;

namespace CoachCompanion.Core;

public sealed record PipelineStats(
    long CoachSegments,
    long ClientSegments,
    long SegmentsDiscarded,
    int SegmentsWaiting,
    long Transcribed,
    long Empty,
    long FramesDropped);

/// <summary>
/// Wires two audio sources (coach = microphone, client = meeting-app loopback) through
/// per-channel segmenters into one speech-to-text engine and publishes utterances.
/// Audio threads only enqueue; segmentation runs on one task per source; STT on one task.
/// </summary>
public sealed class TranscriptionPipeline : IAsyncDisposable
{
    private readonly IAudioSource _coach;
    private readonly IAudioSource _client;
    private readonly ISpeechToText _stt;
    private readonly TranscriptWriter? _writer;
    private readonly SegmenterOptions _options;
    private readonly Func<IVoiceActivityDetector> _vadFactory;
    private readonly Channel<AudioSegment> _segments = Channel.CreateBounded<AudioSegment>(
        new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<Utterance> _utterances = Channel.CreateUnbounded<Utterance>(
        new UnboundedChannelOptions { SingleWriter = true });
    private readonly List<Task> _tasks = new();
    private readonly CancellationTokenSource _cts = new();
    private UtteranceSegmenter? _coachSegmenter;
    private UtteranceSegmenter? _clientSegmenter;
    private DateTimeOffset _startedAt;
    private int _nextId;
    private long _transcribed;
    private long _empty;

    public TranscriptionPipeline(
        IAudioSource coachSource,
        IAudioSource clientSource,
        ISpeechToText speechToText,
        string? transcriptJsonlPath = null,
        SegmenterOptions? options = null,
        Func<IVoiceActivityDetector>? vadFactory = null)
    {
        _coach = coachSource;
        _client = clientSource;
        _stt = speechToText;
        _writer = transcriptJsonlPath is null ? null : new TranscriptWriter(transcriptJsonlPath);
        _options = options ?? new SegmenterOptions();
        _vadFactory = vadFactory ?? (() => new EnergyVad());
    }

    /// <summary>Utterances in the order they were transcribed. Completes after <see cref="StopAsync"/>.</summary>
    public ChannelReader<Utterance> Utterances => _utterances.Reader;

    public PipelineStats Stats => new(
        _coachSegmenter?.SegmentsEmitted ?? 0,
        _clientSegmenter?.SegmentsEmitted ?? 0,
        (_coachSegmenter?.SegmentsDiscarded ?? 0) + (_clientSegmenter?.SegmentsDiscarded ?? 0),
        _segments.Reader.Count,
        Interlocked.Read(ref _transcribed),
        Interlocked.Read(ref _empty),
        _coach.FramesDropped + _client.FramesDropped);

    public void Start()
    {
        if (_tasks.Count > 0)
        {
            throw new InvalidOperationException("Already started.");
        }

        _startedAt = DateTimeOffset.Now;
        _coachSegmenter = new UtteranceSegmenter("coach", _vadFactory(), _options);
        _clientSegmenter = new UtteranceSegmenter("client", _vadFactory(), _options);

        _client.Start();
        _coach.Start();

        var coachTask = SegmentAsync(_coach, _coachSegmenter);
        var clientTask = SegmentAsync(_client, _clientSegmenter);
        _tasks.Add(coachTask);
        _tasks.Add(clientTask);
        _tasks.Add(Task.WhenAll(coachTask, clientTask).ContinueWith(_ => _segments.Writer.TryComplete(), TaskScheduler.Default));
        _tasks.Add(TranscribeAsync());
    }

    /// <summary>Stops capture, flushes pending speech, waits for STT to drain, completes the utterance channel.</summary>
    public async Task StopAsync()
    {
        _coach.Stop();
        _client.Stop();
        try
        {
            await Task.WhenAll(_tasks).ConfigureAwait(false);
        }
        finally
        {
            _utterances.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _segments.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // best-effort teardown
        }

        _utterances.Writer.TryComplete();
        _writer?.Dispose();
        _cts.Dispose();
    }

    private async Task SegmentAsync(IAudioSource source, UtteranceSegmenter segmenter)
    {
        try
        {
            await foreach (var frame in source.Frames.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var segment = segmenter.Push(frame);
                if (segment is not null)
                {
                    await _segments.Writer.WriteAsync(segment, _cts.Token).ConfigureAwait(false);
                }
            }

            var tail = segmenter.Flush();
            if (tail is not null)
            {
                await _segments.Writer.WriteAsync(tail, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task TranscribeAsync()
    {
        try
        {
            await foreach (var segment in _segments.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var result = await _stt.TranscribeAsync(segment.Samples, _cts.Token).ConfigureAwait(false);
                if (result.Text.Length == 0)
                {
                    Interlocked.Increment(ref _empty);
                    continue;
                }

                Interlocked.Increment(ref _transcribed);
                var speaker = segment.Channel == "coach" ? Speaker.Coach : Speaker.Client;
                var utterance = new Utterance(
                    Interlocked.Increment(ref _nextId),
                    speaker,
                    result.Text,
                    segment.Start,
                    segment.End,
                    _startedAt + segment.Start);
                _writer?.Write(utterance);
                _utterances.Writer.TryWrite(utterance);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
