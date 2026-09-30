using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;
using System.Net.Http;

namespace CouchCompanion.Services
{
    public class WhisperService : IDisposable
    {
        private WhisperFactory _whisperFactory;
        private WhisperProcessor _whisperProcessor;
        private GgmlType _modelType = GgmlType.Base; // Default model type
        private string _modelName = "ggml-base.bin"; // Default model name

        public event EventHandler<SegmentData> OnNewSegment;

        public WhisperService()
        {
            // Ensure model directory exists
            Directory.CreateDirectory("models");
            _modelName = Path.Combine("models", _modelName);
        }

        public async Task InitializeWhisperAsync()
        {
            if (!File.Exists(_modelName))
            {
                Console.WriteLine($"Downloading Whisper model {_modelName}...");
                try
                {
                    using (var httpClient = new HttpClient())
                    {
                        var downloader = new WhisperGgmlDownloader(httpClient);
                        using var modelStream = await downloader.GetGgmlModelAsync(_modelType);
                        using var fileWriter = File.OpenWrite(_modelName);
                        await modelStream.CopyToAsync(fileWriter);
                    }
                    Console.WriteLine("Whisper model downloaded successfully.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error downloading Whisper model: {ex.Message}");
                    throw; // Re-throw to propagate the error
                }
            }
            else
            {
                Console.WriteLine($"Whisper model {_modelName} already exists.");
            }

            try
            {
                Console.WriteLine($"Initializing WhisperFactory from path: {_modelName}");
                _whisperFactory = WhisperFactory.FromPath(_modelName);
                Console.WriteLine("WhisperFactory initialized. Creating processor...");
                _whisperProcessor = _whisperFactory.CreateBuilder()
                    .WithLanguage("auto")
                    .WithSegmentEventHandler((SegmentData e) => OnNewSegment?.Invoke(this, e))
                    .Build();
                Console.WriteLine("WhisperProcessor created successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing Whisper: {ex.Message}");
                throw; // Re-throw to propagate the error
            }
        }

        public async Task ProcessAudioAsync(float[] audioSamples, CancellationToken cancellationToken)
        {
            if (_whisperProcessor == null)
            {
                await InitializeWhisperAsync();
            }

            // Whisper.net expects a stream, so we convert the float array to a MemoryStream
            using (var audioStream = new MemoryStream())
            {
                foreach (var sample in audioSamples)
                {
                    byte[] bytes = BitConverter.GetBytes(sample);
                    audioStream.Write(bytes, 0, bytes.Length);
                }
                audioStream.Seek(0, SeekOrigin.Begin);

                await foreach (var segment in _whisperProcessor.ProcessAsync(audioStream, cancellationToken))
                {
                    OnNewSegment?.Invoke(this, segment);
                }
            }
        }

        public void Dispose()
        {
            _whisperProcessor?.Dispose();
            _whisperFactory?.Dispose();
        }
    }
}
