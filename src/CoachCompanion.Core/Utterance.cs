namespace CoachCompanion.Core;

public enum Speaker
{
    Coach,
    Client,
}

/// <summary>
/// One transcribed utterance. <see cref="Start"/> and <see cref="End"/> are positions in the
/// speaker's audio stream since the session started; <see cref="WallClock"/> is the absolute
/// time the utterance started.
/// </summary>
public sealed record Utterance(
    int Id,
    Speaker Speaker,
    string Text,
    TimeSpan Start,
    TimeSpan End,
    DateTimeOffset WallClock);
