using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace CoachCompanion.Audio.Windows;

/// <summary>
/// Captures the audio rendered by one process tree with WASAPI process loopback
/// (AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK), the mechanism behind the OBS
/// "Application Audio Capture" source. No driver, no routing change, the target keeps its
/// own device settings. Requires Windows 10 build 20348 or later, or Windows 11.
/// </summary>
public sealed class ProcessLoopbackSource : IAudioSource
{
    private const string VirtualDevicePath = "VAD\\Process_Loopback";
    private const ushort VtBlob = 0x41;
    private const int ActivationTypeProcessLoopback = 1;
    private const int ModeIncludeTargetProcessTree = 0;
    private const int ModeExcludeTargetProcessTree = 1;
    private const long BufferDuration100Ns = 200_000; // 20 ms
    private static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly WaveFormat CaptureFormat = new(48000, 16, 2);

    private readonly int _pid;
    private readonly bool _includeTree;
    private readonly Channel<AudioFrame> _channel = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(500) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ManualResetEventSlim _stopRequested = new();

    private AudioClient? _client;
    private Thread? _thread;
    private FrameConverter? _converter;

    public ProcessLoopbackSource(int processId, bool includeProcessTree = true)
    {
        _pid = processId;
        _includeTree = includeProcessTree;
        string name;
        try
        {
            name = Process.GetProcessById(processId).ProcessName;
        }
        catch
        {
            name = "?";
        }

        Name = $"loopback: {name} (pid {processId}{(includeProcessTree ? ", tree" : string.Empty)})";
    }

    public string Name { get; }

    public ChannelReader<AudioFrame> Frames => _channel.Reader;

    public long FramesDropped => _converter?.FramesDropped ?? 0;

    public void Start()
    {
        if (_client is not null)
        {
            throw new InvalidOperationException("Already started.");
        }

        // ActivateAudioInterfaceAsync wants an MTA thread; a UI thread is STA, so hop to the pool.
        var client = Task.Run(() => Activate(_pid, _includeTree)).GetAwaiter().GetResult();
        client.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
            BufferDuration100Ns,
            0,
            CaptureFormat,
            Guid.Empty);

        var sampleReady = new AutoResetEvent(false);
        client.SetEventHandle(sampleReady.SafeWaitHandle.DangerousGetHandle());
        var captureClient = client.AudioCaptureClient;
        var converter = new FrameConverter(CaptureFormat, _channel.Writer);
        _client = client;
        _converter = converter;
        client.Start();

        _thread = new Thread(() => CaptureLoop(captureClient, sampleReady, converter))
        {
            IsBackground = true,
            Name = Name,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stopRequested.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        Stop();
        _client?.Dispose();
        _stopRequested.Dispose();
    }

    private void CaptureLoop(AudioCaptureClient capture, AutoResetEvent sampleReady, FrameConverter converter)
    {
        int blockAlign = CaptureFormat.BlockAlign;
        byte[] buffer = [];
        try
        {
            while (!_stopRequested.IsSet)
            {
                // The event only fires while the target renders audio, hence the timeout.
                sampleReady.WaitOne(100);
                while (capture.GetNextPacketSize() > 0)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    int bytes = frames * blockAlign;
                    if (buffer.Length < bytes)
                    {
                        buffer = new byte[bytes];
                    }

                    if ((flags & AudioClientBufferFlags.Silent) != 0)
                    {
                        Array.Clear(buffer, 0, bytes);
                    }
                    else
                    {
                        Marshal.Copy(data, buffer, 0, bytes);
                    }

                    capture.ReleaseBuffer(frames);
                    converter.Push(buffer.AsSpan(0, bytes));
                }
            }

            _client?.Stop();
            _channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _channel.Writer.TryComplete(ex);
        }
    }

    private static AudioClient Activate(int pid, bool includeTree)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = (uint)pid,
            ProcessLoopbackMode = includeTree ? ModeIncludeTargetProcessTree : ModeExcludeTargetProcessTree,
        };

        int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr pParams = Marshal.AllocHGlobal(paramsSize);
        IntPtr pPropVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activationParams, pParams, false);
            var propVariant = new PropVariantBlob { Vt = VtBlob, BlobSize = (uint)paramsSize, BlobData = pParams };
            Marshal.StructureToPtr(propVariant, pPropVariant, false);

            var handler = new ActivationHandler();
            Guid iid = IidIAudioClient;
            ActivateAudioInterfaceAsync(VirtualDevicePath, ref iid, pPropVariant, handler, out var operation);
            if (!handler.Completion.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("ActivateAudioInterfaceAsync did not complete within 5 s.");
            }

            GC.KeepAlive(operation);
            return new AudioClient(handler.Completion.Task.Result);
        }
        finally
        {
            Marshal.FreeHGlobal(pPropVariant);
            Marshal.FreeHGlobal(pParams);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandlerNative completionHandler,
        out IActivateAudioInterfaceAsyncOperationNative activationOperation);

    /// <summary>AUDIOCLIENT_ACTIVATION_PARAMS with its single-member union flattened.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>PROPVARIANT restricted to VT_BLOB (vt, three reserved words, then BLOB {cbSize, pBlobData}).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public IntPtr BlobData;
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandlerNative
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperationNative activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperationNative
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// <summary>Marker interface so the completion handler may be called from any apartment.</summary>
    [ComImport]
    [Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObjectNative
    {
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandlerNative, IAgileObjectNative
    {
        public TaskCompletionSource<IAudioClient> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperationNative activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hr, out object activated);
                if (hr < 0)
                {
                    Completion.TrySetException(Marshal.GetExceptionForHR(hr)
                        ?? new COMException("Process loopback activation failed.", hr));
                    return;
                }

                Completion.TrySetResult((IAudioClient)activated);
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }
        }
    }
}
