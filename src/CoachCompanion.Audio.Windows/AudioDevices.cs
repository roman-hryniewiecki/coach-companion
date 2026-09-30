using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace CoachCompanion.Audio.Windows;

public static class AudioDevices
{
    public sealed record CaptureDevice(string Id, string Name, bool IsDefaultCommunications);

    public sealed record RenderSession(string Device, string Process, int Pid, string State);

    public static IReadOnlyList<CaptureDevice> CaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string defaultId = string.Empty;
        try
        {
            using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            defaultId = def.ID;
        }
        catch (Exception)
        {
            // no capture device at all
        }

        var result = new List<CaptureDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                result.Add(new CaptureDevice(device.ID, device.FriendlyName, device.ID == defaultId));
            }
        }

        return result;
    }

    /// <summary>Audio sessions currently open on render devices, with the owning process. Handy to see who is playing.</summary>
    public static IReadOnlyList<RenderSession> RenderSessions()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<RenderSession>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    int pid = (int)session.GetProcessID;
                    string process;
                    try
                    {
                        process = pid == 0 ? "system" : Process.GetProcessById(pid).ProcessName;
                    }
                    catch
                    {
                        process = "?";
                    }

                    result.Add(new RenderSession(device.FriendlyName, process, pid, session.State.ToString().Replace("AudioSessionState", string.Empty)));
                }
            }
        }

        return result;
    }
}
