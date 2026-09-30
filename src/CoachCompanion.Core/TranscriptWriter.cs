using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoachCompanion.Core;

/// <summary>Appends utterances to a JSON Lines file, one object per line, flushed immediately.</summary>
public sealed class TranscriptWriter : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public TranscriptWriter(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        FilePath = path;
    }

    public string FilePath { get; }

    public void Write(Utterance utterance)
    {
        string line = JsonSerializer.Serialize(utterance, Options);
        lock (_lock)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }
}
