using SoundFlow.Extensions.WebRtc.Apm;

namespace CoachCompanion.Audio.WebRtc;

/// <summary>
/// WebRTC audio processing module (AEC3 desktop mode) over 16 kHz mono pipeline frames.
/// Cross-platform natives ship with the package, so the same code serves Fedora later.
/// WebRTC works in 10 ms blocks, so each 20 ms frame is fed as two blocks. Render and capture
/// calls are serialized with a lock; the native module keeps its own render buffer, so the
/// far end may run ahead of the near end by hundreds of milliseconds.
/// </summary>
public sealed class WebRtcEchoCanceller : IEchoCanceller
{
    private readonly AudioProcessingModule _apm;
    private readonly ApmConfig _config;
    private readonly StreamConfig _streamConfig;
    private readonly object _lock = new();
    private readonly int _block;
    private readonly float[][] _farIn;
    private readonly float[][] _farOut;
    private readonly float[][] _nearIn;
    private readonly float[][] _nearOut;

    public WebRtcEchoCanceller(bool noiseSuppression = false, bool highPassFilter = true, int delayHintMs = 0)
    {
        _block = AudioProcessingModule.GetFrameSize(PipelineFormat.SampleRate);
        if (_block <= 0 || PipelineFormat.FrameSamples % _block != 0)
        {
            throw new InvalidOperationException($"Unexpected WebRTC block size {_block} for {PipelineFormat.SampleRate} Hz.");
        }

        _farIn = [new float[_block]];
        _farOut = [new float[_block]];
        _nearIn = [new float[_block]];
        _nearOut = [new float[_block]];

        _apm = new AudioProcessingModule();
        _config = new ApmConfig();
        _config.SetEchoCanceller(true, mobileMode: false);
        _config.SetNoiseSuppression(noiseSuppression, NoiseSuppressionLevel.Moderate);
        _config.SetHighPassFilter(highPassFilter);
        _config.SetGainController1(false, GainControlMode.AdaptiveDigital, 3, 9, true);
        _config.SetGainController2(false);
        _config.SetPreAmplifier(false, 1f);
        Check(_apm.ApplyConfig(_config), "ApplyConfig");
        Check(_apm.Initialize(), "Initialize");
        _streamConfig = new StreamConfig(PipelineFormat.SampleRate, 1);
        _apm.SetStreamDelayMs(delayHintMs);
        Description = $"WebRTC AEC3 ({PipelineFormat.SampleRate} Hz, {_block}-sample blocks, ns={(noiseSuppression ? "on" : "off")}, hpf={(highPassFilter ? "on" : "off")})";
    }

    public string Description { get; }

    public long Errors { get; private set; }

    public void FeedFarEnd(ReadOnlySpan<float> frame)
    {
        lock (_lock)
        {
            for (int offset = 0; offset < PipelineFormat.FrameSamples; offset += _block)
            {
                frame.Slice(offset, _block).CopyTo(_farIn[0]);
                if (_apm.ProcessReverseStream(_farIn, _streamConfig, _streamConfig, _farOut) != ApmError.NoError)
                {
                    Errors++;
                }
            }
        }
    }

    public void ProcessNearEnd(Span<float> frame)
    {
        lock (_lock)
        {
            for (int offset = 0; offset < PipelineFormat.FrameSamples; offset += _block)
            {
                frame.Slice(offset, _block).CopyTo(_nearIn[0]);
                if (_apm.ProcessStream(_nearIn, _streamConfig, _streamConfig, _nearOut) != ApmError.NoError)
                {
                    Errors++;
                    continue; // leave the block unprocessed
                }

                _nearOut[0].AsSpan().CopyTo(frame.Slice(offset, _block));
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _streamConfig.Dispose();
            _config.Dispose();
            _apm.Dispose();
        }
    }

    private static void Check(ApmError error, string step)
    {
        if (error != ApmError.NoError)
        {
            throw new InvalidOperationException($"WebRTC APM {step} failed: {error}");
        }
    }
}
