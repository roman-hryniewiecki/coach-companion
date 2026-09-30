using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using System;
using System.Diagnostics;
using System.IO;

namespace CouchCompanion;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private StreamWriter _logWriter;

    public App()
    {
        // Initialize logging to a file
        try
        {
            _logWriter = new StreamWriter("app_log.txt", append: true) { AutoFlush = true };
            Console.SetOut(_logWriter);
            Console.SetError(_logWriter);
        }
        catch (Exception ex)
        {
            // Fallback if file logging fails
            Console.WriteLine($"Error setting up file logging: {ex.Message}");
        }

        Console.WriteLine($"[{DateTime.Now}] App constructor started.");
        this.DispatcherUnhandledException += App_DispatcherUnhandledException;
        Console.WriteLine($"[{DateTime.Now}] App constructor finished.");
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Log the exception
        Console.WriteLine($"Unhandled exception: {e.Exception.Message}");
        Console.WriteLine($"Stack Trace: {e.Exception.StackTrace}");

        // Optionally, show a message box to the user
        MessageBox.Show("An unhandled exception occurred: " + e.Exception.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);

        // Prevent the application from crashing
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Console.WriteLine($"[{DateTime.Now}] App exiting.");
        _logWriter?.Close();
        _logWriter?.Dispose();
        base.OnExit(e);
    }
}
