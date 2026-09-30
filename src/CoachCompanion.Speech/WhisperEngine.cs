using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace CoachCompanion.Speech;

/// <summary>
/// Local Whisper (whisper.cpp through Whisper.net). One processor, serialized with a gate,
/// shared by both channels: segments are short, so a queue beats two GPU contexts.
/// </summary>
public sealed partial class WhisperEngine : ISpeechToText
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly double _noSpeechThreshold;

    public WhisperEngine(string modelPath, string language = "en", bool preferGpu = true, double noSpeechThreshold = 0.6)
    {
        RuntimeOptions.RuntimeLibraryOrder = preferGpu
            ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cuda, RuntimeLibrary.Cpu]
            : [RuntimeLibrary.Cpu];

        _noSpeechThreshold = noSpeechThreshold;
        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder()
            .WithLanguage(language)
            .WithThreads(Math.Max(1, Environment.ProcessorCount / 2))
            .WithNoContext()
            .Build();
        Description = $"whisper {Path.GetFileName(modelPath)} on {RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown runtime"}, language {language}";
    }

    public string Description { get; }

    public async Task<TranscriptionResult> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var text = new StringBuilder();
            double noSpeech = 0;
            int segments = 0;
            await foreach (var segment in _processor.ProcessAsync(samples16kMono, cancellationToken).ConfigureAwait(false))
            {
                segments++;
                noSpeech = Math.Max(noSpeech, segment.NoSpeechProbability);
                text.Append(segment.Text);
            }

            string cleaned = Clean(text.ToString());
            if (segments == 0 || cleaned.Length == 0 || noSpeech > _noSpeechThreshold)
            {
                return new TranscriptionResult(string.Empty, noSpeech);
            }

            return new TranscriptionResult(cleaned, noSpeech);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _processor.Dispose();
        _factory.Dispose();
        _gate.Dispose();
    }

    /// <summary>Drops non-speech annotations such as [BLANK_AUDIO], (music), *laughs*.</summary>
    private static string Clean(string text)
    {
        string withoutAnnotations = AnnotationPattern().Replace(text, " ");
        return WhitespacePattern().Replace(withoutAnnotations, " ").Trim();
    }

    [GeneratedRegex(@"[\[\(\*][^\]\)\*]*[\]\)\*]")]
    private static partial Regex AnnotationPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
