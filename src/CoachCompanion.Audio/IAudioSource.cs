using System.Threading.Channels;

namespace CoachCompanion.Audio;

/// <summary>
/// A live audio source (microphone, application loopback, later PipeWire on Linux) that
/// delivers pipeline-format frames through a channel. Implementations complete the channel
/// after <see cref="Stop"/> or on a fatal capture error (with the exception attached).
/// </summary>
public interface IAudioSource : IDisposable
{
    string Name { get; }

    ChannelReader<AudioFrame> Frames { get; }

    /// <summary>Number of frames the consumer was too slow to take (dropped at the source).</summary>
    long FramesDropped { get; }

    void Start();

    void Stop();
}
