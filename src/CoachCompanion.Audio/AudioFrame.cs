namespace CoachCompanion.Audio;

/// <summary>Pipeline audio format: 16 kHz, mono, float32 in [-1, 1], 20 ms frames.</summary>
public static class PipelineFormat
{
    public const int SampleRate = 16000;
    public const int FrameMilliseconds = 20;
    public const int FrameSamples = SampleRate * FrameMilliseconds / 1000; // 320
}

/// <summary>
/// One 20 ms frame of pipeline audio. <see cref="Position"/> is the stream position measured
/// from the moment the source started, derived from the number of frames emitted.
/// </summary>
public readonly record struct AudioFrame(float[] Samples, TimeSpan Position);
