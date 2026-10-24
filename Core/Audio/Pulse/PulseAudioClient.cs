using System.Runtime.InteropServices;

namespace CAudioVisualizer.Core;

internal sealed class PulseAudioDevice
{
    /// PulseAudio identifier, e.g. alsa_output.pci-0000_00_1f.3.analog-stereo.
    public string Name { get; init; } = "";

    /// Human readable name, e.g. Built-in Audio Analog Stereo.
    public string Description { get; init; } = "";

    /// True for playback devices.
    public bool IsSink { get; init; }
}

/// Result of a device enumeration.
internal sealed class PulseAudioSnapshot
{
    public List<PulseAudioDevice> Sinks { get; } = new();
    public List<PulseAudioDevice> Sources { get; } = new();
    public string DefaultSinkName { get; set; } = "";
    public string DefaultSourceName { get; set; } = "";

    /// Monitor sources (loopback taps of the playback devices) are named "&lt;sink&gt;.monitor".
    public static string MonitorNameFor(string sinkName) => sinkName + ".monitor";

    public bool IsMonitorSource(string sourceName) => Sinks.Any(sink => sourceName == MonitorNameFor(sink.Name));

    /// Name of the source that has to be recorded in order to capture the given device id.
    public string ResolveCaptureSource(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            string sinkName = DefaultSinkName;
            if (string.IsNullOrEmpty(sinkName))
                sinkName = Sinks.FirstOrDefault()?.Name ?? "";

            return sinkName + ".monitor";
        }

        // Known sources (monitors and microphones) can be recorded directly.
        if (Sources.Any(source => source.Name == deviceId))
            return deviceId;

        // Otherwise the id refers to a playback device, so record its monitor.
        if (Sinks.Any(sink => sink.Name == deviceId))
            return MonitorNameFor(deviceId);

        return deviceId;
    }
}

internal sealed class PulseAudioConnection : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    private readonly PulseAudioNative.ContextNotifyCallback _stateCallback;
    private readonly PulseAudioNative.DeviceInfoCallback _deviceInfoCallback;
    private readonly PulseAudioNative.ServerInfoCallback _serverInfoCallback;
    private readonly ManualResetEventSlim _stateChanged = new(false);
    private readonly ManualResetEventSlim _deviceQueryCompleted = new(false);
    private readonly ManualResetEventSlim _serverQueryCompleted = new(false);
    private readonly object _queryLock = new();
    private readonly List<PulseAudioDevice> _queriedDevices = new();

    private IntPtr _mainloop;
    private IntPtr _context;
    private IntPtr _pendingOperation;
    private string _defaultSinkName = "";
    private string _defaultSourceName = "";
    private PulseAudioNative.ContextState _state = PulseAudioNative.ContextState.Unconnected;
    private bool _disposed;

    /// The connected pa_context. Only valid while holding the main loop lock.
    public IntPtr Context => _context;

    /// The threaded main loop. Only valid while holding the main loop lock.
    public IntPtr Mainloop => _mainloop;

    public PulseAudioConnection(string applicationName)
    {
        _stateCallback = OnContextStateChanged;
        _deviceInfoCallback = OnDeviceInfo;
        _serverInfoCallback = OnServerInfo;

        _mainloop = PulseAudioNative.pa_threaded_mainloop_new();
        if (_mainloop == IntPtr.Zero)
        {
            throw new AudioCaptureException("Could not create a PulseAudio main loop. Is libpulse installed?");
        }

        PulseAudioNative.pa_threaded_mainloop_start(_mainloop);

        // Every PulseAudio call has to run while the main loop lock is held, otherwise the
        // main loop thread may touch the same objects concurrently.
        IntPtr api = PulseAudioNative.pa_threaded_mainloop_get_api(_mainloop);

        string? connectError = null;
        Lock(() =>
        {
            _context = PulseAudioNative.pa_context_new(api, applicationName);
            if (_context == IntPtr.Zero)
            {
                connectError = "Could not create a PulseAudio context.";
                return;
            }

            // The delegates have to be kept alive for as long as libpulse may call them.
            PulseAudioNative.pa_context_set_state_callback(_context, _stateCallback, IntPtr.Zero);
            PulseAudioNative.pa_threaded_mainloop_signal(_mainloop, 0);

            if (PulseAudioNative.pa_context_connect(_context, null, 0, IntPtr.Zero) < 0)
                connectError = $"Could not connect to PulseAudio: {GetError()}";
        });

        if (connectError != null)
        {
            ReleaseAll();
            throw new AudioCaptureException(connectError);
        }

        // The state callback reports every intermediate state, so keep waiting until the
        // context either becomes ready or fails.
        DateTime deadline = DateTime.UtcNow + ConnectTimeout;
        while (_state is not (PulseAudioNative.ContextState.Ready
                or PulseAudioNative.ContextState.Failed
                or PulseAudioNative.ContextState.Terminated))
        {
            if (DateTime.UtcNow > deadline)
            {
                ReleaseAll();
                throw new AudioCaptureException("Timed out while connecting to PulseAudio.");
            }

            _stateChanged.Wait(TimeSpan.FromMilliseconds(20));
            _stateChanged.Reset();
        }

        if (_state != PulseAudioNative.ContextState.Ready)
        {
            string error = GetError();
            ReleaseAll();
            throw new AudioCaptureException($"Could not connect to PulseAudio: {error}");
        }
    }

    private void OnContextStateChanged(IntPtr context, IntPtr userData)
    {
        _state = PulseAudioNative.pa_context_get_state(context);
        _stateChanged.Set();
    }

    public void Lock(Action action)
    {
        if (_disposed)
            return;

        RunLocked(action);
    }

    private void RunLocked(Action action)
    {
        IntPtr mainloop = _mainloop;
        if (mainloop == IntPtr.Zero)
            return;

        PulseAudioNative.pa_threaded_mainloop_lock(mainloop);
        try
        {
            action();
        }
        finally
        {
            PulseAudioNative.pa_threaded_mainloop_unlock(mainloop);
        }
    }

    /// Queries sink, source and server information from the daemon.
    public PulseAudioSnapshot QuerySnapshot()
    {
        lock (_queryLock)
        {
            var snapshot = new PulseAudioSnapshot();

            foreach (var sink in QueryDeviceList(sinks: true))
            {
                if (!string.IsNullOrEmpty(sink.Name))
                    snapshot.Sinks.Add(sink);
            }

            foreach (var source in QueryDeviceList(sinks: false))
            {
                if (!string.IsNullOrEmpty(source.Name))
                    snapshot.Sources.Add(source);
            }

            QueryServerInfo(snapshot);

            return snapshot;
        }
    }

    private List<PulseAudioDevice> QueryDeviceList(bool sinks)
    {
        _queriedDevices.Clear();
        _deviceQueryCompleted.Reset();

        Lock(() =>
        {
            IntPtr operation = sinks
                ? PulseAudioNative.pa_context_get_sink_info_list(_context, _deviceInfoCallback, IntPtr.Zero)
                : PulseAudioNative.pa_context_get_source_info_list(_context, _deviceInfoCallback, IntPtr.Zero);

            if (operation == IntPtr.Zero)
                throw new AudioCaptureException($"Could not query PulseAudio devices: {GetError()}");

            _pendingOperation = operation;
        });

        // The end-of-list callback is dispatched by the main loop thread.
        bool completed = _deviceQueryCompleted.Wait(QueryTimeout);
        UnrefPendingOperation();

        if (!completed)
            throw new AudioCaptureException("Timed out while querying PulseAudio devices.");

        return _queriedDevices;
    }

    private void OnDeviceInfo(IntPtr context, IntPtr info, int endOfList, IntPtr userData)
    {
        if (info != IntPtr.Zero)
        {
            var deviceInfo = Marshal.PtrToStructure<PulseAudioNative.DeviceInfo>(info);
            _queriedDevices.Add(new PulseAudioDevice
            {
                Name = PulseAudioNative.ReadUtf8(deviceInfo.Name) ?? "",
                Description = PulseAudioNative.ReadUtf8(deviceInfo.Description) ?? ""
            });
        }

        if (endOfList != 0)
            _deviceQueryCompleted.Set();
    }

    private void QueryServerInfo(PulseAudioSnapshot snapshot)
    {
        _defaultSinkName = "";
        _defaultSourceName = "";
        _serverQueryCompleted.Reset();

        Lock(() =>
        {
            IntPtr operation = PulseAudioNative.pa_context_get_server_info(_context, _serverInfoCallback, IntPtr.Zero);
            if (operation != IntPtr.Zero)
                _pendingOperation = operation;
        });

        bool completed = _serverQueryCompleted.Wait(QueryTimeout);
        UnrefPendingOperation();

        if (!completed)
            return;

        snapshot.DefaultSinkName = _defaultSinkName;
        snapshot.DefaultSourceName = _defaultSourceName;
    }

    private void OnServerInfo(IntPtr context, IntPtr info, IntPtr userData)
    {
        if (info != IntPtr.Zero)
        {
            var serverInfo = Marshal.PtrToStructure<PulseAudioNative.ServerInfo>(info);
            _defaultSinkName = PulseAudioNative.ReadUtf8(serverInfo.DefaultSinkName) ?? "";
            _defaultSourceName = PulseAudioNative.ReadUtf8(serverInfo.DefaultSourceName) ?? "";
        }

        // This callback is invoked exactly once, without an end-of-list marker.
        _serverQueryCompleted.Set();
    }

    private void UnrefPendingOperation()
    {
        IntPtr operation = _pendingOperation;
        if (operation == IntPtr.Zero)
            return;

        _pendingOperation = IntPtr.Zero;
        Lock(() => PulseAudioNative.pa_operation_unref(operation));
    }

    public string GetError()
    {
        int error = PulseAudioNative.pa_context_errno(_context);
        string? message = PulseAudioNative.ReadUtf8(PulseAudioNative.pa_strerror(error));
        return string.IsNullOrEmpty(message) ? "unknown error" : message;
    }

    private void ReleaseAll()
    {
        IntPtr context = _context;
        _context = IntPtr.Zero;

        if (context != IntPtr.Zero)
        {
            RunLocked(() =>
            {
                PulseAudioNative.pa_context_disconnect(context);
                PulseAudioNative.pa_context_unref(context);
            });
        }

        IntPtr mainloop = _mainloop;
        _mainloop = IntPtr.Zero;

        if (mainloop != IntPtr.Zero)
        {
            // Both calls must happen without the main loop lock held.
            PulseAudioNative.pa_threaded_mainloop_stop(mainloop);
            PulseAudioNative.pa_threaded_mainloop_free(mainloop);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // ReleaseAll needs the lock, so only mark the connection as disposed afterwards.
        ReleaseAll();
        _disposed = true;
        _stateChanged.Dispose();
        _deviceQueryCompleted.Dispose();
        _serverQueryCompleted.Dispose();
    }
}

/// Creates PulseAudio connections for device enumeration and audio capture.
internal static class PulseAudioClient
{
    private static readonly object SyncRoot = new();
    private static PulseAudioConnection? _sharedConnection;

    private static PulseAudioConnection? SharedConnection
    {
        get
        {
            if (!PulseAudioNative.IsAvailable())
                return null;

            lock (SyncRoot)
            {
                if (_sharedConnection != null)
                    return _sharedConnection;

                try
                {
                    _sharedConnection = new PulseAudioConnection("CAudioVisualizer");
                    return _sharedConnection;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"PulseAudio is not available: {ex.Message}");
                    return null;
                }
            }
        }
    }

    public static PulseAudioSnapshot TryQuerySnapshot()
    {
        var connection = SharedConnection;
        if (connection == null)
            return new PulseAudioSnapshot();

        try
        {
            return connection.QuerySnapshot();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to enumerate PulseAudio devices: {ex.Message}");
            return new PulseAudioSnapshot();
        }
    }

    public static PulseAudioCapture? TryCreateCapture(string deviceId, string deviceName)
    {
        if (!PulseAudioNative.IsAvailable())
        {
            Console.WriteLine(
                "libpulse could not be loaded. Install the PulseAudio client library " +
                "(e.g. 'pipewire-pulse' or 'libpulse' on Arch based distributions).");
            return null;
        }

        PulseAudioSnapshot snapshot = TryQuerySnapshot();
        string sourceName = snapshot.ResolveCaptureSource(deviceId);

        if (string.IsNullOrEmpty(sourceName))
        {
            Console.WriteLine("No PulseAudio source available for capture.");
            return null;
        }

        try
        {
            return new PulseAudioCapture(sourceName, deviceName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create PulseAudio capture for '{sourceName}': {ex.Message}");
            return null;
        }
    }
}
