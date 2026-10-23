using System.Runtime.InteropServices;

namespace CAudioVisualizer.Core;

internal static class PulseAudioNative
{
    internal const string LibraryName = "libpulse.so.0";

    /// pa_sample_format_t: PA_SAMPLE_FLOAT32LE
    internal const int PaSampleFloat32Le = 5;

    /// Special device names understood by the PulseAudio server.
    internal const string DefaultSinkName = "@DEFAULT_SINK@";

    internal const string DefaultSourceName = "@DEFAULT_SOURCE@";

    /// pa_context_state_t
    internal enum ContextState
    {
        Unconnected = 0,
        Connecting = 1,
        Authorizing = 2,
        SettingName = 3,
        Ready = 4,
        Failed = 5,
        Terminated = 6
    }

    /// pa_stream_state_t
    internal enum StreamState
    {
        Unconnected = 0,
        Creating = 1,
        Ready = 2,
        Failed = 3,
        Terminated = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    /// pa_buffer_attr
    [StructLayout(LayoutKind.Sequential)]
    internal struct BufferAttr
    {
        public uint MaxLength;
        public uint TLength;
        public uint PreBuf;
        public uint MinReq;
        public uint FragSize;
    }

    /// Leading fields of pa_sink_info / pa_source_info. The native structures are much
    /// larger, but the prefix layout is identical for both and is all we need.
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfo
    {
        public IntPtr Name;
        public uint Index;
        public IntPtr Description;
    }

    /// Leading fields of pa_server_info (default sink/source names).
    [StructLayout(LayoutKind.Sequential)]
    internal struct ServerInfo
    {
        public IntPtr UserName;
        public IntPtr HostName;
        public IntPtr ServerVersion;
        public IntPtr ServerName;
        public SampleSpec SampleSpec;
        public IntPtr DefaultSinkName;
        public IntPtr DefaultSourceName;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ContextNotifyCallback(IntPtr context, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void StreamNotifyCallback(IntPtr stream, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void StreamRequestCallback(IntPtr stream, nuint bytes, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void DeviceInfoCallback(IntPtr context, IntPtr deviceInfo, int endOfList, IntPtr userData);

    /// pa_server_info_cb_t - unlike the device callbacks this one has no 'end of list' argument.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ServerInfoCallback(IntPtr context, IntPtr serverInfo, IntPtr userData);

    // --- main loop ---

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pa_threaded_mainloop_new();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pa_threaded_mainloop_free(IntPtr mainloop);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pa_threaded_mainloop_start(IntPtr mainloop);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pa_threaded_mainloop_stop(IntPtr mainloop);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pa_threaded_mainloop_lock(IntPtr mainloop);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pa_threaded_mainloop_unlock(IntPtr mainloop);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pa_threaded_mainloop_get_api(IntPtr mainloop);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pa_threaded_mainloop_signal(IntPtr mainloop, int waitForAccept);

    // --- context ---

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pa_context_new(IntPtr mainloopApi, string name);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pa_context_unref(IntPtr context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pa_context_connect(IntPtr context, string? server, int flags, IntPtr spawnApi);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pa_context_disconnect(IntPtr context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pa_context_set_state_callback(IntPtr context, ContextNotifyCallback? callback, IntPtr userData);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ContextState pa_context_get_state(IntPtr context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pa_context_errno(IntPtr context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pa_context_get_server(IntPtr context);

    // --- introspection ---

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pa_context_get_server_info(IntPtr context, ServerInfoCallback callback, IntPtr userData);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pa_context_get_sink_info_list(IntPtr context, DeviceInfoCallback callback, IntPtr userData);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pa_context_get_source_info_list(IntPtr context, DeviceInfoCallback callback, IntPtr userData);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pa_operation_unref(IntPtr operation);

    // --- streams ---

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pa_stream_new(IntPtr context, string name, ref SampleSpec sampleSpec, IntPtr channelMap);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pa_stream_unref(IntPtr stream);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pa_stream_connect_record(IntPtr stream, string? device, ref BufferAttr bufferAttributes, int flags);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pa_stream_disconnect(IntPtr stream);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pa_stream_set_state_callback(IntPtr stream, StreamNotifyCallback? callback, IntPtr userData);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pa_stream_set_read_callback(IntPtr stream, StreamRequestCallback? callback, IntPtr userData);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern StreamState pa_stream_get_state(IntPtr stream);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nuint pa_stream_readable_size(IntPtr stream);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pa_stream_peek(IntPtr stream, out IntPtr data, out nuint bytes);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pa_stream_drop(IntPtr stream);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pa_stream_get_sample_spec(IntPtr stream);

    // --- misc ---

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pa_strerror(int error);

    internal static bool IsAvailable()
    {
        try
        {
            return NativeLibrary.TryLoad(LibraryName, out var handle) || NativeLibrary.TryLoad("libpulse.so", out handle);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string? ReadUtf8(IntPtr pointer)
    {
        return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
    }
}
