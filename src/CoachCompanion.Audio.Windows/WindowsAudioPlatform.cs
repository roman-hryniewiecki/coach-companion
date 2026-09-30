using NAudio.CoreAudioApi;

namespace CoachCompanion.Audio.Windows;

public sealed class WindowsAudioPlatform : IAudioPlatform
{
    public IReadOnlyList<AudioDeviceInfo> CaptureDevices() =>
        AudioDevices.CaptureDevices()
            .Select(d => new AudioDeviceInfo(d.Id, d.Name, d.IsDefaultCommunications))
            .ToList();

    public IAudioSource OpenMicrophone(string? deviceId = null)
    {
        if (deviceId is null)
        {
            return MicCaptureSource.Default();
        }

        using var enumerator = new MMDeviceEnumerator();
        return new MicCaptureSource(enumerator.GetDevice(deviceId));
    }

    public IAudioSource OpenApplicationLoopback(int processId) => new ProcessLoopbackSource(processId);
}
