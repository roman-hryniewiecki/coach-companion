namespace CoachCompanion.Audio;

public interface IVoiceActivityDetector
{
    /// <summary>Classifies one 20 ms pipeline frame. Implementations may keep state across calls.</summary>
    bool IsSpeech(ReadOnlySpan<float> frame);
}

/// <summary>
/// Energy VAD with an adaptive noise floor. Sufficient for the loopback channel, which is
/// digitally silent between utterances, and for a quiet room. The plan is to swap in Silero
/// VAD (ONNX) for the microphone channel; this class exists so the pipeline works without a
/// model download.
/// </summary>
public sealed class EnergyVad : IVoiceActivityDetector
{
    private readonly double _marginDb;
    private readonly double _absoluteMinDb;
    private double _floorDb = -70;

    /// <param name="marginDb">How far above the tracked noise floor a frame must be to count as speech.</param>
    /// <param name="absoluteMinDb">Frames quieter than this are never speech, whatever the floor.</param>
    public EnergyVad(double marginDb = 12, double absoluteMinDb = -50)
    {
        _marginDb = marginDb;
        _absoluteMinDb = absoluteMinDb;
    }

    public double NoiseFloorDb => _floorDb;

    public bool IsSpeech(ReadOnlySpan<float> frame)
    {
        double sumSquares = 0;
        foreach (float v in frame)
        {
            sumSquares += v * v;
        }

        double db = 10 * Math.Log10(sumSquares / Math.Max(1, frame.Length) + 1e-12);
        bool speech = db > Math.Max(_absoluteMinDb, _floorDb + _marginDb);
        if (!speech)
        {
            _floorDb += (db - _floorDb) * 0.05; // track the floor on non-speech frames
        }
        else
        {
            _floorDb += 0.02; // slow upward drift (1 dB/s) so a stale, too-low floor recovers
        }

        return speech;
    }
}
