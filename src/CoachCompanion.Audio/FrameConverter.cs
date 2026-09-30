using System.Buffers.Binary;
using System.Threading.Channels;
using NAudio.Dsp;
using NAudio.Wave;

namespace CoachCompanion.Audio;

/// <summary>
/// Converts raw PCM from a capture device (16/24/32-bit integer or 32-bit float, any channel
/// count, any sample rate) into 20 ms mono float frames at the pipeline rate and pushes them
/// into a channel. One instance per source, fed from that source's capture thread only.
/// The resampler keeps its state across calls, so chunk boundaries do not cause artifacts.
/// </summary>
public sealed class FrameConverter
{
    private static readonly Guid KsDataFormatSubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly WaveFormat _input;
    private readonly bool _inputIsFloat;
    private readonly ChannelWriter<AudioFrame> _writer;
    private readonly WdlResampler? _resampler;
    private readonly float[] _frame = new float[PipelineFormat.FrameSamples];
    private int _frameFill;
    private long _framesEmitted;
    private float[] _mono = [];
    private float[] _resampled = [];

    public FrameConverter(WaveFormat input, ChannelWriter<AudioFrame> writer)
    {
        _input = input;
        _writer = writer;
        _inputIsFloat = input.Encoding == WaveFormatEncoding.IeeeFloat
            || (input is WaveFormatExtensible ext && ext.SubFormat == KsDataFormatSubtypeIeeeFloat);

        if (input.SampleRate != PipelineFormat.SampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true); // input driven: we push whatever the device gives us
            _resampler.SetRates(input.SampleRate, PipelineFormat.SampleRate);
        }
    }

    public long FramesDropped { get; private set; }

    public void Push(ReadOnlySpan<byte> pcm)
    {
        int channels = _input.Channels;
        int bytesPerSample = _input.BitsPerSample / 8;
        int inputFrames = pcm.Length / (bytesPerSample * channels);
        if (inputFrames == 0)
        {
            return;
        }

        if (_mono.Length < inputFrames)
        {
            _mono = new float[inputFrames];
        }

        for (int f = 0; f < inputFrames; f++)
        {
            float acc = 0f;
            for (int c = 0; c < channels; c++)
            {
                acc += Decode(pcm, (f * channels + c) * bytesPerSample, bytesPerSample);
            }

            _mono[f] = acc / channels;
        }

        ReadOnlySpan<float> samples;
        if (_resampler is null)
        {
            samples = _mono.AsSpan(0, inputFrames);
        }
        else
        {
            int wanted = _resampler.ResamplePrepare(inputFrames, 1, out float[] inBuffer, out int inOffset);
            int n = Math.Min(wanted, inputFrames);
            Array.Copy(_mono, 0, inBuffer, inOffset, n);
            int capacity = (int)((long)inputFrames * PipelineFormat.SampleRate / _input.SampleRate) + 64;
            if (_resampled.Length < capacity)
            {
                _resampled = new float[capacity];
            }

            int produced = _resampler.ResampleOut(_resampled, 0, n, capacity, 1);
            samples = _resampled.AsSpan(0, produced);
        }

        int idx = 0;
        while (idx < samples.Length)
        {
            int take = Math.Min(PipelineFormat.FrameSamples - _frameFill, samples.Length - idx);
            samples.Slice(idx, take).CopyTo(_frame.AsSpan(_frameFill));
            _frameFill += take;
            idx += take;
            if (_frameFill == PipelineFormat.FrameSamples)
            {
                var position = TimeSpan.FromMilliseconds(_framesEmitted * PipelineFormat.FrameMilliseconds);
                if (!_writer.TryWrite(new AudioFrame((float[])_frame.Clone(), position)))
                {
                    FramesDropped++;
                }

                _framesEmitted++;
                _frameFill = 0;
            }
        }
    }

    private float Decode(ReadOnlySpan<byte> pcm, int offset, int bytesPerSample)
    {
        if (_inputIsFloat)
        {
            return BinaryPrimitives.ReadSingleLittleEndian(pcm.Slice(offset, 4));
        }

        switch (bytesPerSample)
        {
            case 2:
                return BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(offset, 2)) / 32768f;
            case 3:
                int v = pcm[offset] | (pcm[offset + 1] << 8) | (pcm[offset + 2] << 16);
                if (v >= 0x800000)
                {
                    v -= 0x1000000;
                }

                return v / 8388608f;
            case 4:
                return BinaryPrimitives.ReadInt32LittleEndian(pcm.Slice(offset, 4)) / 2147483648f;
            default:
                return 0f;
        }
    }
}
