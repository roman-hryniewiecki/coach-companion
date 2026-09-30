using System.Threading.Channels;

namespace CoachCompanion.Audio;

/// <summary>
/// Acoustic echo canceller over pipeline frames. The far end is what the speakers play, which
/// for us is the meeting application's loopback; the near end is the microphone, cleaned in place.
/// <see cref="FeedFarEnd"/> and <see cref="ProcessNearEnd"/> are called from different threads;
/// implementations must tolerate that, and the far end must be fed ahead of the near-end frames
/// that contain its echo.
/// </summary>
public interface IEchoCanceller : IDisposable
{
    string Description { get; }

    /// <summary>Feeds one 20 ms far-end (render) frame.</summary>
    void FeedFarEnd(ReadOnlySpan<float> frame);

    /// <summary>Cleans one 20 ms near-end (capture) frame in place.</summary>
    void ProcessNearEnd(Span<float> frame);
}

/// <summary>
/// Drives an <see cref="IEchoCanceller"/> from a microphone and a loopback source and exposes two
/// <see cref="IAudioSource"/> facades: <see cref="Coach"/> (cleaned mic) and <see cref="Client"/>
/// (loopback, unchanged), so the rest of the pipeline is unaware of the AEC.
/// Loopback frames are fed to the canceller as they arrive; microphone frames pass through a
/// short delay line first, so the far end is always ahead of the echo it explains. On this
/// machine the loopback reference arrives a little after the echo when paired by arrival, and
/// WebRTC AEC3 cannot handle a negative delay, so the lead is what makes cancellation work.
/// </summary>
public sealed class EchoCancellationStage : IDisposable
{
    private readonly IAudioSource _mic;
    private readonly IAudioSource _farEnd;
    private readonly IEchoCanceller _canceller;
    private readonly int _leadFrames;
    private readonly Channel<AudioFrame> _coachOut = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(500) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<AudioFrame> _clientOut = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(500) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private int _started;
    private long _coachDropped;
    private long _clientDropped;

    /// <param name="leadMilliseconds">How far ahead of the microphone the far end is fed (delay applied to the mic).</param>
    public EchoCancellationStage(IAudioSource microphone, IAudioSource farEnd, IEchoCanceller canceller, int leadMilliseconds = 100)
    {
        _mic = microphone;
        _farEnd = farEnd;
        _canceller = canceller;
        _leadFrames = Math.Max(0, leadMilliseconds / PipelineFormat.FrameMilliseconds);
        Coach = new Facade($"aec({microphone.Name})", _coachOut.Reader, () => _coachDropped + microphone.FramesDropped, StartAll, microphone.Stop);
        Client = new Facade(farEnd.Name, _clientOut.Reader, () => _clientDropped + farEnd.FramesDropped, StartAll, farEnd.Stop);
    }

    public IAudioSource Coach { get; }

    public IAudioSource Client { get; }

    public long FarEndFrames { get; private set; }

    public long FramesProcessed { get; private set; }

    public void Dispose()
    {
        _canceller.Dispose();
    }

    private void StartAll()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _farEnd.Start();
        _mic.Start();
        _ = Task.Run(PumpFarEndAsync);
        _ = Task.Run(ProcessNearEndAsync);
    }

    private async Task PumpFarEndAsync()
    {
        try
        {
            await foreach (var frame in _farEnd.Frames.ReadAllAsync().ConfigureAwait(false))
            {
                _canceller.FeedFarEnd(frame.Samples);
                FarEndFrames++;
                if (!_clientOut.Writer.TryWrite(frame))
                {
                    _clientDropped++;
                }
            }

            _clientOut.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _clientOut.Writer.TryComplete(ex);
        }
    }

    private async Task ProcessNearEndAsync()
    {
        var delayLine = new Queue<AudioFrame>(_leadFrames + 1);
        try
        {
            await foreach (var frame in _mic.Frames.ReadAllAsync().ConfigureAwait(false))
            {
                delayLine.Enqueue(frame);
                if (delayLine.Count <= _leadFrames)
                {
                    continue;
                }

                Emit(delayLine.Dequeue());
            }

            while (delayLine.Count > 0)
            {
                Emit(delayLine.Dequeue());
            }

            _coachOut.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _coachOut.Writer.TryComplete(ex);
        }
    }

    private void Emit(AudioFrame frame)
    {
        _canceller.ProcessNearEnd(frame.Samples);
        FramesProcessed++;
        if (!_coachOut.Writer.TryWrite(frame))
        {
            _coachDropped++;
        }
    }

    private sealed class Facade : IAudioSource
    {
        private readonly Func<long> _dropped;
        private readonly Action _start;
        private readonly Action _stop;

        public Facade(string name, ChannelReader<AudioFrame> frames, Func<long> dropped, Action start, Action stop)
        {
            Name = name;
            Frames = frames;
            _dropped = dropped;
            _start = start;
            _stop = stop;
        }

        public string Name { get; }

        public ChannelReader<AudioFrame> Frames { get; }

        public long FramesDropped => _dropped();

        public void Start() => _start();

        public void Stop() => _stop();

        public void Dispose()
        {
            // underlying sources are owned by whoever created the stage
        }
    }
}
