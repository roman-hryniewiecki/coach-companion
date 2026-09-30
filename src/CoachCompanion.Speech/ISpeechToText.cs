namespace CoachCompanion.Speech;

/// <summary>Result for one audio segment. <see cref="Text"/> is empty when nothing usable was heard.</summary>
public sealed record TranscriptionResult(string Text, double NoSpeechProbability);

/// <summary>
/// Speech-to-text over one utterance of pipeline audio (16 kHz mono float). Implementations
/// must be safe to call from several tasks; they may serialize internally.
/// </summary>
public interface ISpeechToText : IDisposable
{
    string Description { get; }

    Task<TranscriptionResult> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken = default);
}
