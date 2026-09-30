using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using CouchCompanion.Models;

namespace CouchCompanion.Services
{
    public class AudioCaptureService : IDisposable
    {
        private WasapiLoopbackCapture _waveIn;
        private BufferedWaveProvider _bufferedWaveProvider;
        private WaveFormat _targetWaveFormat;
        private CancellationTokenSource _cancellationTokenSource;

        public event EventHandler<FloatArrayEventArgs> AudioDataAvailable;

        public AudioCaptureService()
        {
            // Target format for Whisper: 16kHz, mono, float32
            _targetWaveFormat = new WaveFormat(16000, 16, 1); // 16-bit PCM, will convert to float later
        }

        public async Task StartCaptureAsync(Process process)
        {
            StopCapture(); // Stop any existing capture

            _cancellationTokenSource = new CancellationTokenSource();

            await Task.Run(() =>
            {
                try
                {
                    var device = GetAudioDeviceForProcess(process);
                    if (device == null)
                    {
                        Console.WriteLine($"No audio device found for process: {process.ProcessName}. Trying default render device.");
                        // Fallback to default render device if process-specific device not found
                        using (var defaultEnumerator = new MMDeviceEnumerator())
                        {
                            device = defaultEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                            if (device == null)
                            {
                                Console.WriteLine("No default render device found, cannot capture audio.");
                                return;
                            }
                        }
                    }
                    Console.WriteLine($"Using audio device: {device.FriendlyName} (ID: {device.ID}) for process: {process.ProcessName}");

                    _waveIn = new WasapiLoopbackCapture(device);
                    _waveIn.DataAvailable += OnDataAvailable;
                    _waveIn.RecordingStopped += OnRecordingStopped;

                    _bufferedWaveProvider = new BufferedWaveProvider(_waveIn.WaveFormat)
                    {
                        DiscardOnBufferOverflow = true
                    };

                    _waveIn.StartRecording();
                    Console.WriteLine($"Started audio capture for process: {process.ProcessName}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error starting audio capture: {ex.Message}");
                }
            }, _cancellationTokenSource.Token);
        }

        public void StopCapture()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }

            if (_waveIn != null)
            {
                _waveIn.StopRecording();
                _waveIn.Dispose();
                _waveIn = null;
            }
            if (_bufferedWaveProvider != null)
            {
                _bufferedWaveProvider = null;
            }
            Debug.WriteLine("Stopped audio capture.");
        }

        private MMDevice GetAudioDeviceForProcess(Process process)
        {
            using (var enumerator = new MMDeviceEnumerator())
            {
                // Get all active render devices (speakers, headphones, etc.)
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                foreach (var device in devices)
                {
                    // Check if the process has an audio session on this device
                    var sessionManager = device.AudioSessionManager;
                    if (sessionManager != null)
                    {
                        var sessionEnumerator = sessionManager.Sessions;
                        for (int i = 0; i < sessionEnumerator.Count; i++)
                        {
                            var session = sessionEnumerator[i];
                            if (session.GetProcessID == process.Id)
                            {
                                return device; // Found the device associated with the process
                            }
                            session.Dispose();
                        }
                    }
                    device.Dispose();
                }
            }
            return null; // No device found for the process
        }


        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            Console.WriteLine($"Audio data available: {e.BytesRecorded} bytes.");
            if (_bufferedWaveProvider != null)
            {
                _bufferedWaveProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);
                // Process buffered audio immediately if there's any data
                ProcessBufferedAudio();
            }
        }

        private void ProcessBufferedAudio()
        {
            // Process audio if there's any data in the buffer
            if (_bufferedWaveProvider.BufferedBytes > 0)
            {
                // Process in smaller chunks to avoid large delays, e.g., 100ms of audio
                int chunkSize = _bufferedWaveProvider.WaveFormat.AverageBytesPerSecond / 10; // 100ms chunk
                if (_bufferedWaveProvider.BufferedBytes < chunkSize)
                {
                    chunkSize = _bufferedWaveProvider.BufferedBytes;
                }

                var buffer = new byte[chunkSize];
                var bytesRead = _bufferedWaveProvider.Read(buffer, 0, buffer.Length);

                // Convert to target format (16kHz, mono, float32)
                using (var ms = new MemoryStream(buffer, 0, bytesRead))
                using (var rs = new RawSourceWaveStream(ms, _bufferedWaveProvider.WaveFormat))
                using (var resampler = new MediaFoundationResampler(rs, _targetWaveFormat))
                {
                    // Ensure the resampler is correctly initialized
                    var sampleProvider = resampler.ToSampleProvider();
                    var floatBuffer = new float[sampleProvider.WaveFormat.SampleRate * sampleProvider.WaveFormat.Channels];
                    int samplesRead = sampleProvider.Read(floatBuffer, 0, floatBuffer.Length);
                    Console.WriteLine($"Processed {samplesRead} float samples.");
                    AudioDataAvailable?.Invoke(this, new FloatArrayEventArgs(floatBuffer, samplesRead * sizeof(float)));
                }
            }
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                Console.WriteLine($"Audio capture stopped with error: {e.Exception.Message}");
            }
            else
            {
                Console.WriteLine("Audio capture stopped normally.");
            }
        }

        public void Dispose()
        {
            StopCapture();
        }
    }
}
