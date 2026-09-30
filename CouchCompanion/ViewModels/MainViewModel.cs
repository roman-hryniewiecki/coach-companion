using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CouchCompanion.Models;
using CouchCompanion.Services;
using NAudio.Wave;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Input;

namespace CouchCompanion.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        [ObservableProperty]
        private ObservableCollection<ScreenInfo> _availableScreens;

        [ObservableProperty]
        private ScreenInfo _selectedScreen;

        [ObservableProperty]
        private ObservableCollection<WindowInfo> _availableWindows;

        [ObservableProperty]
        private WindowInfo _selectedWindow;

        [ObservableProperty]
        private string _transcriptionText;

        [ObservableProperty]
        private bool _isCapturing;

        [ObservableProperty]
        private string _attachButtonText;

        private AudioCaptureService _audioCaptureService;
        private WhisperService _whisperService;
        private CancellationTokenSource _whisperCancellationTokenSource;

        public ICommand RefreshCommand { get; }
        public ICommand ToggleCaptureCommand { get; }

        public MainViewModel()
        {
            AvailableScreens = new ObservableCollection<ScreenInfo>();
            AvailableWindows = new ObservableCollection<WindowInfo>();
            TranscriptionText = string.Empty;
            IsCapturing = false;
            AttachButtonText = "Attach";

            _audioCaptureService = new AudioCaptureService();
            _audioCaptureService.AudioDataAvailable += OnAudioDataAvailable;

            _whisperService = new WhisperService();
            _whisperService.OnNewSegment += OnNewWhisperSegment;

            RefreshCommand = new RelayCommand(RefreshData);
            ToggleCaptureCommand = new AsyncRelayCommand(ToggleCaptureAsync);

            RefreshData();
        }

        private void RefreshData()
        {
            LoadScreens();
            LoadWindows();
        }

        private void LoadScreens()
        {
            AvailableScreens.Clear();
            foreach (var screen in Screen.AllScreens)
            {
                AvailableScreens.Add(new ScreenInfo(screen));
            }
            SelectedScreen = AvailableScreens.FirstOrDefault();
        }

        partial void OnIsCapturingChanged(bool value)
        {
            AttachButtonText = value ? "Stop" : "Attach";
        }

        private void LoadWindows()
        {
            AvailableWindows.Clear();
            EnumWindows(new EnumWindowsProc(EnumWindowsCallback), IntPtr.Zero);
            // Sort windows alphabetically by title
            var sortedWindows = AvailableWindows.OrderBy(w => w.Title).ToList();
            AvailableWindows.Clear();
            foreach (var window in sortedWindows)
            {
                AvailableWindows.Add(window);
            }
            SelectedWindow = AvailableWindows.FirstOrDefault();
        }

        private bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0)
            {
                return true;
            }

            StringBuilder sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();

            GetWindowThreadProcessId(hwnd, out uint processId);
            Process process = Process.GetProcessById((int)processId);

            AvailableWindows.Add(new WindowInfo(hwnd, title, process));
            return true;
        }

        private async Task ToggleCaptureAsync()
        {
            if (IsCapturing)
            {
                StopCapture();
            }
            else
            {
                await StartCaptureAsync();
            }
        }

        private async Task StartCaptureAsync()
        {
            if (SelectedWindow == null)
            {
                TranscriptionText = "Please select a window to capture audio from.";
                return;
            }

            TranscriptionText = "Initializing Whisper and starting audio capture...";
            IsCapturing = true;

            try
            {
                await _whisperService.InitializeWhisperAsync();
                _whisperCancellationTokenSource = new CancellationTokenSource();
                await _audioCaptureService.StartCaptureAsync(SelectedWindow.Process);
                TranscriptionText = "Capturing audio and transcribing...";
            }
            catch (Exception ex)
            {
                TranscriptionText = $"Error: {ex.Message}";
                IsCapturing = false;
            }
        }

        private void StopCapture()
        {
            _audioCaptureService.StopCapture();
            _whisperCancellationTokenSource?.Cancel();
            IsCapturing = false;
            TranscriptionText = "Audio capture stopped.";
        }

        private async void OnAudioDataAvailable(object sender, FloatArrayEventArgs e)
        {
            if (_whisperCancellationTokenSource != null && !_whisperCancellationTokenSource.IsCancellationRequested)
            {
                // Assuming e.Buffer contains float[] as per AudioCaptureService
                float[] audioSamples = e.Buffer;
                await _whisperService.ProcessAudioAsync(audioSamples, _whisperCancellationTokenSource.Token);
                // Debug.WriteLine($"Captured {audioSamples.Length} float samples.");
                // System.Windows.Application.Current.Dispatcher.Invoke(() =>
                // {
                //     TranscriptionText = $"Capturing audio... Processed {audioSamples.Length} samples.";
                // });
            }
        }

        private void OnNewWhisperSegment(object sender, Whisper.net.SegmentData e)
        {
            // Update UI on the main thread
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                TranscriptionText += $"{e.Start} ==> {e.End} : {e.Text}\n";
            });
        }

        // P/Invoke declarations
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public void Dispose()
        {
            _audioCaptureService?.Dispose();
            _whisperService?.Dispose();
            _whisperCancellationTokenSource?.Dispose();
        }
    }
}
