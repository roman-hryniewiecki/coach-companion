using System.Diagnostics;

namespace CoachCompanion.Core;

public static class ProcessLocator
{
    /// <summary>
    /// The oldest running process with the given name, which for meeting apps is the root of
    /// the process tree that process loopback should capture. Returns <c>null</c> when none runs.
    /// </summary>
    public static Process? FindRoot(string processName)
    {
        if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            processName = processName[..^4];
        }

        Process? best = null;
        DateTime bestStart = DateTime.MaxValue;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (best is null || process.StartTime < bestStart)
                {
                    bestStart = process.StartTime;
                    best = process;
                }
            }
            catch (Exception)
            {
                best ??= process; // StartTime can be access denied; still a valid target
            }
        }

        return best;
    }
}
