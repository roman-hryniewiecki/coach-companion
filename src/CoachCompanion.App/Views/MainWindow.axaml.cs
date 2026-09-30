using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using CoachCompanion.App.ViewModels;

namespace CoachCompanion.App.Views;

public partial class MainWindow : Window
{
    private bool _closeApproved;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Utterances.CollectionChanged += OnUtterancesChanged;
            }
        };
        Closing += OnClosing;
    }

    private void OnUtterancesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.UIThread.Post(() => TranscriptScroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved || DataContext is not MainViewModel { IsRunning: true } vm)
        {
            return;
        }

        e.Cancel = true;
        await vm.StopCommand.ExecuteAsync(null);
        _closeApproved = true;
        Close();
    }
}
