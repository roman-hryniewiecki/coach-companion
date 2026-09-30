namespace CoachCompanion.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// Platform entry point for audio capture. Windows implements it with WASAPI; a PipeWire
/// implementation is planned for Fedora. Everything above this interface is platform-neutral.
/// </summary>
public interface IAudioPlatform
{
    IReadOnlyList<AudioDeviceInfo> CaptureDevices();

    /// <summary>Opens a microphone; <c>null</c> selects the default communications device.</summary>
    IAudioSource OpenMicrophone(string? deviceId = null);

    /// <summary>Opens the audio rendered by one application (process tree).</summary>
    IAudioSource OpenApplicationLoopback(int processId);
}
