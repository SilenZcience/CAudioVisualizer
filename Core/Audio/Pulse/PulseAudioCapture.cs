using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace CAudioVisualizer.Core;

internal sealed class PulseAudioCapture : IAudioCapture
{
    private const int RequestedSampleRate = 48000;
    private const int RequestedChannels = 2;
    private const int BitsPerSampleValue = 32;

    private const int BlockSize = 1024;

    private const int MaxQueuedBlocks = 8;
    private const int ReadBufferSize = 8192;

    private const int FragmentMilliseconds = 10;

    /// Target latency in milliseconds. PulseAudio's default record buffer is a lot larger,
    /// which would delay the visualized audio by seconds, so ask for a small one.
    private const int TargetLatencyMilliseconds = 40;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    private static readonly TimeSpan StreamTimeout = TimeSpan.FromSeconds(5);

    private readonly PulseAudioConnection _connection;
    private readonly PulseAudioNative.StreamNotifyCallback _stateCallback;
    private readonly PulseAudioNative.StreamRequestCallback _readCallback;
    private readonly ManualResetEventSlim _streamReady = new(false);
    private readonly BlockingCollection<float[]> _blocks = new(new ConcurrentQueue<float[]>(), MaxQueuedBlocks);
    private readonly Thread _dispatchThread;

    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private readonly float[] _pending = new float[BlockSize * 4];
    private readonly object _drainLock = new();

    private GCHandle _selfHandle;
    private IntPtr _stream;
    private PulseAudioNative.StreamState _streamState = PulseAudioNative.StreamState.Unconnected;
    private int _pendingCount;
    private Timer? _pollTimer;
    private volatile bool _running;
    private bool _tearingDown;
    private bool _disposed;

    public string DeviceName { get; }

    public int SampleRate { get; private set; } = RequestedSampleRate;
    public int Channels { get; private set; } = RequestedChannels;
    public int BitsPerSample => BitsPerSampleValue;
    public int AverageBytesPerSecond => SampleRate * Channels * BitsPerSampleValue / 8;

    public event EventHandler<AudioSamplesEventArgs>? DataAvailable;

    public PulseAudioCapture(string sourceName, string deviceName)
    {
        DeviceName = deviceName;

        _stateCallback = OnStreamStateChanged;
        _readCallback = OnReadRequest;
        _selfHandle = GCHandle.Alloc(this);

        _connection = new PulseAudioConnection("CAudioVisualizer (capture)");

        try
        {
            var sampleSpec = new PulseAudioNative.SampleSpec
            {
                Format = PulseAudioNative.PaSampleFloat32Le,
                Rate = RequestedSampleRate,
                Channels = RequestedChannels
            };

            var bufferAttributes = CreateBufferAttributes(RequestedSampleRate, RequestedChannels);

            _connection.Lock(() =>
            {
                _stream = PulseAudioNative.pa_stream_new(_connection.Context, "CAudioVisualizer", ref sampleSpec, IntPtr.Zero);
                if (_stream == IntPtr.Zero)
                    throw new AudioCaptureException($"Could not create PulseAudio stream for '{sourceName}': {_connection.GetError()}");

                PulseAudioNative.pa_stream_set_state_callback(_stream, _stateCallback, IntPtr.Zero);
                PulseAudioNative.pa_stream_set_read_callback(_stream, _readCallback, GCHandle.ToIntPtr(_selfHandle));

                if (PulseAudioNative.pa_stream_connect_record(_stream, sourceName, ref bufferAttributes, 0) < 0)
                {
                    throw new AudioCaptureException($"Could not connect to PulseAudio source '{sourceName}': {_connection.GetError()}");
                }
            });

            if (!_streamReady.Wait(StreamTimeout))
                throw new AudioCaptureException($"Timed out while connecting to PulseAudio source '{sourceName}'.");

            if (_streamState != PulseAudioNative.StreamState.Ready)
                throw new AudioCaptureException($"PulseAudio stream for '{sourceName}' failed: {_connection.GetError()}");

            UpdateNegotiatedSampleSpec();
        }
        catch
        {
            ReleaseStream();
            _connection.Dispose();
            FreeSelfHandle();
            throw;
        }

        _dispatchThread = new Thread(DispatchLoop)
        {
            IsBackground = true,
            Name = "PulseAudio capture dispatch"
        };
        _dispatchThread.Start();
    }

    private static PulseAudioNative.BufferAttr CreateBufferAttributes(int sampleRate, int channels)
    {
        int bytesPerMilliSecond = Math.Max(1, sampleRate * channels * sizeof(float) / 1000);

        int fragment = Align(bytesPerMilliSecond * FragmentMilliseconds);
        int targetLength = Align(bytesPerMilliSecond * TargetLatencyMilliseconds);

        // PulseAudio wants the target length to be a multiple of the fragment size.
        targetLength = fragment * Math.Max(1, targetLength / fragment);

        return new PulseAudioNative.BufferAttr
        {
            FragSize = (uint)fragment,
            TLength = (uint)targetLength,
            MaxLength = (uint)(targetLength * 4),
            MinReq = (uint)fragment,
            PreBuf = unchecked((uint)-1) // let the server decide
        };

        static int Align(int value) => Math.Max(sizeof(float) * 2, value & ~7);
    }

    private void OnStreamStateChanged(IntPtr stream, IntPtr userData)
    {
        _streamState = PulseAudioNative.pa_stream_get_state(stream);

        if (_streamState is PulseAudioNative.StreamState.Ready
            or PulseAudioNative.StreamState.Failed
            or PulseAudioNative.StreamState.Terminated)
        {
            _streamReady.Set();
        }

        if (_streamState is PulseAudioNative.StreamState.Failed or PulseAudioNative.StreamState.Terminated)
        {
            // Disconnecting the stream on purpose also ends up here, that is not an error.
            if (!_tearingDown && !_disposed)
                Console.WriteLine($"PulseAudio capture stream for '{DeviceName}' stopped: {_connection.GetError()}");
        }
    }

    private void OnReadRequest(IntPtr stream, nuint bytes, IntPtr userData)
    {
        if (!_running || _disposed)
            return;

        try
        {
            // Never block the main loop thread here: if the poll timer is already draining
            // the very same buffer, the data is handled either way.
            if (!Monitor.TryEnter(_drainLock))
                return;

            try
            {
                DrainStream(stream);
            }
            finally
            {
                Monitor.Exit(_drainLock);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while reading from PulseAudio: {ex.Message}");
        }
    }

    private void Poll()
    {
        if (!_running || _disposed)
            return;

        IntPtr stream = _stream;
        if (stream == IntPtr.Zero)
            return;

        try
        {
            // The buffered size is tracked on the client side, so this is a cheap check
            // which avoids taking the main loop lock when there is nothing to do.
            if (PulseAudioNative.pa_stream_readable_size(stream) == 0)
                return;

            if (!Monitor.TryEnter(_drainLock))
                return;

            try
            {
                _connection.Lock(() =>
                {
                    if (!_disposed && _stream != IntPtr.Zero)
                        DrainStream(_stream);
                });
            }
            finally
            {
                Monitor.Exit(_drainLock);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while reading from PulseAudio: {ex.Message}");
        }
    }

    private void DrainStream(IntPtr stream)
    {
        int channels = Math.Max(1, Channels);
        int bytesPerFrame = channels * sizeof(float);

        // Consume everything the server has buffered. pa_stream_peek() hands out one
        // contiguous block which has to be discarded again with pa_stream_drop().
        while (true)
        {
            if (PulseAudioNative.pa_stream_peek(stream, out IntPtr data, out nuint available) < 0)
                break;

            if (data == IntPtr.Zero)
            {
                // A null pointer with a non zero size marks a hole in the buffer, which has
                // to be dropped as well. Zero on both means the buffer ran empty.
                if (available == 0)
                    break;

                PulseAudioNative.pa_stream_drop(stream);
                continue;
            }

            int total = (int)available;
            int offset = 0;

            while (offset < total)
            {
                int segment = Math.Min(_readBuffer.Length, total - offset);
                segment -= segment % bytesPerFrame;
                if (segment <= 0)
                    break;

                Marshal.Copy(data + offset, _readBuffer, 0, segment);

                int frames = segment / bytesPerFrame;
                for (int frame = 0; frame < frames; frame++)
                {
                    if (_pendingCount >= _pending.Length)
                        PublishPendingSamples();

                    float sum = 0f;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        sum += BitConverter.ToSingle(_readBuffer, (frame * channels + channel) * sizeof(float));
                    }

                    float mono = sum / channels;
                    _pending[_pendingCount++] = mono;
                }

                offset += segment;
            }

            PulseAudioNative.pa_stream_drop(stream);
        }

        if (_pendingCount > 0)
            PublishPendingSamples();
    }

    private void PublishPendingSamples()
    {
        if (_pendingCount == 0)
            return;

        var block = new float[_pendingCount];
        Array.Copy(_pending, block, _pendingCount);
        _pendingCount = 0;

        if (_blocks.TryAdd(block))
            return;

        // Consumer is too slow, drop the oldest block to stay responsive.
        _blocks.TryTake(out _);
        _blocks.TryAdd(block);
    }

    private void DispatchLoop()
    {
        try
        {
            foreach (float[] block in _blocks.GetConsumingEnumerable())
            {
                DataAvailable?.Invoke(this, new AudioSamplesEventArgs(block, block.Length));
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
        catch (InvalidOperationException)
        {
            // Shutting down.
        }
    }

    public void StartRecording()
    {
        if (_disposed)
            return;

        _running = true;
        _pollTimer = new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);

        Console.WriteLine($"PulseAudio capture started for '{DeviceName}' " +
                          $"({SampleRate} Hz, {Channels} ch, {BitsPerSample} bit)");
    }

    public void StopRecording()
    {
        if (!_running)
            return;

        _running = false;
        _pollTimer?.Dispose();
        _pollTimer = null;
        _blocks.CompleteAdding();
        ReleaseStream();
    }

    private void UpdateNegotiatedSampleSpec()
    {
        PulseAudioNative.SampleSpec spec = default;

        _connection.Lock(() =>
        {
            if (_stream == IntPtr.Zero)
                return;

            IntPtr pointer = PulseAudioNative.pa_stream_get_sample_spec(_stream);
            if (pointer != IntPtr.Zero)
                spec = Marshal.PtrToStructure<PulseAudioNative.SampleSpec>(pointer);
        });

        if (spec.Rate != 0)
            SampleRate = (int)spec.Rate;

        if (spec.Channels != 0)
            Channels = spec.Channels;
    }

    private void ReleaseStream()
    {
        IntPtr stream = _stream;
        if (stream == IntPtr.Zero)
            return;

        _stream = IntPtr.Zero;
        _running = false;
        _tearingDown = true;

        _connection.Lock(() =>
        {
            PulseAudioNative.pa_stream_disconnect(stream);
            PulseAudioNative.pa_stream_unref(stream);
        });

        _tearingDown = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _running = false;

        _pollTimer?.Dispose();
        _pollTimer = null;

        if (!_blocks.IsAddingCompleted)
            _blocks.CompleteAdding();

        _dispatchThread.Join(TimeSpan.FromSeconds(1));

        ReleaseStream();
        _connection.Dispose();
        FreeSelfHandle();
    }

    private void FreeSelfHandle()
    {
        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
    }
}
