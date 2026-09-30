namespace CoachCompanion.Audio;

/// <summary>A stretch of speech from one channel, ready for speech-to-text.</summary>
public sealed record AudioSegment(string Channel, float[] Samples, TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}

public sealed record SegmenterOptions
{
    /// <summary>Segments with less speech than this are discarded as noise blips.</summary>
    public int MinSpeechMs { get; init; } = 300;

    /// <summary>Silence after speech that ends an utterance.</summary>
    public int EndSilenceMs { get; init; } = 700;

    /// <summary>Hard cap; longer monologues are cut and continue in the next segment.</summary>
    public int MaxSegmentMs { get; init; } = 15000;

    /// <summary>Audio kept before the first speech frame so word onsets are not clipped.</summary>
    public int PrePadMs { get; init; } = 200;

    /// <summary>Trailing silence kept after the last speech frame.</summary>
    public int PostPadMs { get; init; } = 200;
}

/// <summary>
/// Turns a stream of 20 ms frames into utterance-sized segments using a VAD. Single consumer;
/// call <see cref="Push"/> for every frame in order and <see cref="Flush"/> at end of stream.
/// </summary>
public sealed class UtteranceSegmenter
{
    private readonly string _channel;
    private readonly IVoiceActivityDetector _vad;
    private readonly int _minSpeechFrames;
    private readonly int _endSilenceFrames;
    private readonly int _maxFrames;
    private readonly int _prePadFrames;
    private readonly int _postPadFrames;
    private readonly Queue<AudioFrame> _prePad = new();
    private readonly List<AudioFrame> _current = new();
    private bool _inSpeech;
    private int _speechFrames;
    private int _silenceRun;

    public UtteranceSegmenter(string channel, IVoiceActivityDetector vad, SegmenterOptions? options = null)
    {
        options ??= new SegmenterOptions();
        _channel = channel;
        _vad = vad;
        _minSpeechFrames = Frames(options.MinSpeechMs);
        _endSilenceFrames = Frames(options.EndSilenceMs);
        _maxFrames = Frames(options.MaxSegmentMs);
        _prePadFrames = Frames(options.PrePadMs);
        _postPadFrames = Frames(options.PostPadMs);
    }

    public long SegmentsEmitted { get; private set; }

    public long SegmentsDiscarded { get; private set; }

    /// <summary>Feeds one frame; returns a completed segment when an utterance just ended.</summary>
    public AudioSegment? Push(AudioFrame frame)
    {
        bool speech = _vad.IsSpeech(frame.Samples);
        if (!_inSpeech)
        {
            if (!speech)
            {
                _prePad.Enqueue(frame);
                while (_prePad.Count > _prePadFrames)
                {
                    _prePad.Dequeue();
                }

                return null;
            }

            _inSpeech = true;
            _current.Clear();
            _current.AddRange(_prePad);
            _prePad.Clear();
            _speechFrames = 0;
            _silenceRun = 0;
        }

        _current.Add(frame);
        if (speech)
        {
            _speechFrames++;
            _silenceRun = 0;
        }
        else
        {
            _silenceRun++;
        }

        if (_silenceRun >= _endSilenceFrames)
        {
            return Complete(trimTrailingFrames: _silenceRun - _postPadFrames);
        }

        if (_current.Count >= _maxFrames)
        {
            return Complete(trimTrailingFrames: 0);
        }

        return null;
    }

    /// <summary>Emits whatever speech is pending at end of stream.</summary>
    public AudioSegment? Flush()
    {
        if (!_inSpeech)
        {
            return null;
        }

        return Complete(trimTrailingFrames: 0);
    }

    private AudioSegment? Complete(int trimTrailingFrames)
    {
        _inSpeech = false;
        int count = Math.Max(1, _current.Count - Math.Max(0, trimTrailingFrames));
        AudioSegment? segment = null;
        if (_speechFrames >= _minSpeechFrames)
        {
            segment = Build(count);
            SegmentsEmitted++;
        }
        else
        {
            SegmentsDiscarded++;
        }

        // The tail we did not emit becomes pre-pad for the next utterance.
        _prePad.Clear();
        for (int i = Math.Max(count, _current.Count - _prePadFrames); i < _current.Count; i++)
        {
            _prePad.Enqueue(_current[i]);
        }

        _current.Clear();
        return segment;
    }

    private AudioSegment Build(int count)
    {
        var samples = new float[count * PipelineFormat.FrameSamples];
        for (int i = 0; i < count; i++)
        {
            _current[i].Samples.CopyTo(samples, i * PipelineFormat.FrameSamples);
        }

        var start = _current[0].Position;
        var end = _current[count - 1].Position + TimeSpan.FromMilliseconds(PipelineFormat.FrameMilliseconds);
        return new AudioSegment(_channel, samples, start, end);
    }

    private static int Frames(int milliseconds) => Math.Max(1, milliseconds / PipelineFormat.FrameMilliseconds);
}
