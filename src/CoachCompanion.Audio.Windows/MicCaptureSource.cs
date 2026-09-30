using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CoachCompanion.Audio.Windows;

/// <summary>
/// Shared-mode WASAPI capture of a microphone endpoint. Shared mode means the meeting
/// application keeps using the same microphone at the same time.
/// </summary>
public sealed class MicCaptureSource : IAudioSource
{
    private readonly MMDevice _device;
    private readonly Channel<AudioFrame> _channel = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(500) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

    private WasapiCapture? _capture;
    private FrameConverter? _converter;

    public MicCaptureSource(MMDevice device)
    {
        _device = device;
        Name = "mic: " + device.FriendlyName;
    }

    /// <summary>The default communications capture device (what Windows hands to Zoom by default).</summary>
    public static MicCaptureSource Default()
    {
        using var enumerator = new MMDeviceEnumerator();
        return new MicCaptureSource(enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications));
    }

    /// <summary>First active capture device whose friendly name contains <paramref name="nameSubstring"/>.</summary>
    public static MicCaptureSource ByName(string nameSubstring)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            if (device.FriendlyName.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase))
            {
                return new MicCaptureSource(device);
            }

            device.Dispose();
        }

        throw new InvalidOperationException($"No active capture device contains '{nameSubstring}' in its name.");
    }

    public string Name { get; }

    public ChannelReader<AudioFrame> Frames => _channel.Reader;

    public long FramesDropped => _converter?.FramesDropped ?? 0;

    public WaveFormat? DeviceFormat => _capture?.WaveFormat;

    public void Start()
    {
        if (_capture is not null)
        {
            throw new InvalidOperationException("Already started.");
        }

        var capture = new WasapiCapture(_device, useEventSync: true, audioBufferMillisecondsLength: 20);
        var converter = new FrameConverter(capture.WaveFormat, _channel.Writer);
        capture.DataAvailable += (_, e) => converter.Push(e.Buffer.AsSpan(0, e.BytesRecorded));
        capture.RecordingStopped += (_, e) => _channel.Writer.TryComplete(e.Exception);
        _capture = capture;
        _converter = converter;
        capture.StartRecording();
    }

    public void Stop() => _capture?.StopRecording();

    public void Dispose()
    {
        _capture?.Dispose();
        _device.Dispose();
    }
}
