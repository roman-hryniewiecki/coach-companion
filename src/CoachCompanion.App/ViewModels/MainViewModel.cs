using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CoachCompanion.App.Models;
using CoachCompanion.Audio;
using CoachCompanion.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CoachCompanion.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly SessionRunner? _runner;
    private readonly DispatcherTimer _statsTimer;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedMicrophone;

    [ObservableProperty]
    private TargetApp? _selectedTarget;

    [ObservableProperty]
    private string _processName = "Zoom";

    [ObservableProperty]
    private ModelItem? _selectedModel;

    [ObservableProperty]
    private bool _echoCancellation = true;

    [ObservableProperty]
    private bool _noiseSuppression;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Ready. Start the meeting app, pick it below, then press Start.";

    [ObservableProperty]
    private string _stats = string.Empty;

    /// <summary>Designer constructor.</summary>
    public MainViewModel()
        : this(null)
    {
    }

    public MainViewModel(SessionRunner? runner)
    {
        _runner = runner;
        SessionsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoachCompanion", "sessions");
        foreach (var target in TargetApp.Known)
        {
            Targets.Add(target);
        }

        SelectedTarget = Targets[0];
        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStats());

        if (runner is not null)
        {
            runner.UtteranceReceived += u => Dispatcher.UIThread.Post(() => Utterances.Add(new UtteranceItem(u)));
            runner.StatusChanged += s => Dispatcher.UIThread.Post(() => Status = s);
            Refresh();
        }
    }

    public ObservableCollection<AudioDeviceInfo> Microphones { get; } = new();

    public ObservableCollection<TargetApp> Targets { get; } = new();

    public ObservableCollection<ModelItem> Models { get; } = new();

    public ObservableCollection<UtteranceItem> Utterances { get; } = new();

    public string SessionsDirectory { get; }

    partial void OnSelectedTargetChanged(TargetApp? value)
    {
        if (value is not null && value.ProcessName.Length > 0)
        {
            ProcessName = value.ProcessName;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_runner is null)
        {
            return;
        }

        if (SelectedModel is null)
        {
            Status = "No Whisper model found. Put ggml-*.bin files into a models folder next to the app.";
            return;
        }

        IsBusy = true;
        try
        {
            var options = new SessionOptions
            {
                MicrophoneId = SelectedMicrophone?.Id,
                TargetProcessName = ProcessName.Trim(),
                ModelPath = SelectedModel.Path,
                EchoCancellation = EchoCancellation,
                NoiseSuppression = NoiseSuppression,
                SessionsDirectory = SessionsDirectory,
            };
            await _runner.StartAsync(options);
            IsRunning = true;
            _statsTimer.Start();
        }
        catch (Exception ex)
        {
            Status = $"Could not start: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task StopAsync()
    {
        if (_runner is null)
        {
            return;
        }

        _statsTimer.Stop();
        try
        {
            await _runner.StopAsync();
        }
        catch (Exception ex)
        {
            Status = $"Stop failed: {ex.Message}";
        }

        IsRunning = false;
        UpdateStats();
    }

    [RelayCommand]
    private void Refresh()
    {
        if (_runner is null)
        {
            return;
        }

        var previousMic = SelectedMicrophone?.Id;
        Microphones.Clear();
        foreach (var device in _runner.Platform.CaptureDevices())
        {
            Microphones.Add(device);
        }

        SelectedMicrophone = Microphones.FirstOrDefault(m => m.Id == previousMic)
            ?? Microphones.FirstOrDefault(m => m.IsDefault)
            ?? Microphones.FirstOrDefault();

        var previousModel = SelectedModel?.Path;
        Models.Clear();
        foreach (var model in FindModels())
        {
            Models.Add(model);
        }

        SelectedModel = Models.FirstOrDefault(m => m.Path == previousModel)
            ?? Models.FirstOrDefault(m => m.Name == "small.en")
            ?? Models.FirstOrDefault();
    }

    [RelayCommand]
    private void Clear() => Utterances.Clear();

    [RelayCommand]
    private void OpenSessionsFolder()
    {
        Directory.CreateDirectory(SessionsDirectory);
        Process.Start(new ProcessStartInfo(SessionsDirectory) { UseShellExecute = true });
    }

    private bool CanStart() => !IsRunning && !IsBusy;

    private void UpdateStats()
    {
        var s = _runner?.Stats;
        if (s is null)
        {
            return;
        }

        string aec = _runner?.EchoStage is { } stage
            ? $"  |  AEC {stage.FramesProcessed} mic frames, {stage.FarEndFrames} far-end frames"
            : string.Empty;
        Stats = $"coach segments {s.CoachSegments}, client segments {s.ClientSegments}, waiting {s.SegmentsWaiting}, transcribed {s.Transcribed}, empty {s.Empty}, dropped frames {s.FramesDropped}{aec}";
    }

    /// <summary>Whisper models: a models/ folder found by walking up from the executable, then the user profile.</summary>
    private static ModelItem[] FindModels()
    {
        var candidates = new System.Collections.Generic.List<string>();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            candidates.Add(Path.Combine(dir.FullName, "models"));
            dir = dir.Parent;
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoachCompanion", "models"));

        foreach (var folder in candidates)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var files = Directory.GetFiles(folder, "ggml-*.bin");
            if (files.Length > 0)
            {
                return files
                    .Select(f => new ModelItem(Path.GetFileNameWithoutExtension(f)["ggml-".Length..], f))
                    .OrderBy(m => m.Name)
                    .ToArray();
            }
        }

        return [];
    }
}
