using CoachCompanion.Core;

namespace CoachCompanion.App.Models;

public sealed class UtteranceItem
{
    public UtteranceItem(Utterance utterance)
    {
        Time = utterance.Start.ToString(@"mm\:ss");
        IsCoach = utterance.Speaker == CoachCompanion.Core.Speaker.Coach;
        Speaker = IsCoach ? "Coach" : "Client";
        Text = utterance.Text;
    }

    public string Time { get; }

    public string Speaker { get; }

    public string Text { get; }

    public bool IsCoach { get; }

    public bool IsClient => !IsCoach;
}

/// <summary>A meeting application profile: display name and the root process to capture.</summary>
public sealed record TargetApp(string Name, string ProcessName)
{
    public static readonly TargetApp[] Known =
    [
        new("Zoom", "Zoom"),
        new("Microsoft Teams", "ms-teams"),
        new("Discord", "Discord"),
        new("Google Meet (Chrome)", "chrome"),
        new("Google Meet (Edge)", "msedge"),
        new("Other process...", string.Empty),
    ];
}

public sealed record ModelItem(string Name, string Path);
