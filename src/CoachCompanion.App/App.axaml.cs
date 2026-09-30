using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CoachCompanion.App.ViewModels;
using CoachCompanion.App.Views;
using CoachCompanion.Audio.WebRtc;
using CoachCompanion.Audio.Windows;
using CoachCompanion.Core;

namespace CoachCompanion.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Windows wiring; a Linux build swaps in the PipeWire platform here.
            var runner = new SessionRunner(
                new WindowsAudioPlatform(),
                noiseSuppression => new WebRtcEchoCanceller(noiseSuppression: noiseSuppression));

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(runner),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
