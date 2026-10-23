using NAudio.Wave;

namespace CAudioVisualizer.Core;

internal sealed class NAudioCapture : IAudioCapture
{
    private readonly IWaveIn _waveIn;
    private readonly string _deviceName;
    private float[] _monoBuffer = new float[4096];

    public string DeviceName => _deviceName;

    public int SampleRate => _waveIn.WaveFormat.SampleRate;
    public int Channels => _waveIn.WaveFormat.Channels;
    public int BitsPerSample => _waveIn.WaveFormat.BitsPerSample;
    public int AverageBytesPerSecond => _waveIn.WaveFormat.AverageBytesPerSecond;

    public event EventHandler<AudioSamplesEventArgs>? DataAvailable;

    public NAudioCapture(IWaveIn waveIn, string deviceName)
    {
        _waveIn = waveIn;
        _deviceName = deviceName;
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += (_, e) => Console.WriteLine($"Recording stopped: {e.Exception?.Message ?? "no error"}");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _waveIn.WaveFormat;

        int bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0)
            return;

        int totalSamples = e.BytesRecorded / bytesPerSample;
        if (totalSamples <= 0)
            return;

        int channels = Math.Max(1, format.Channels);
        int frames = totalSamples / channels;

        if (_monoBuffer.Length < frames)
            _monoBuffer = new float[Math.Max(frames, _monoBuffer.Length * 2)];

        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
        bool isPcm16 = format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16;

        for (int frame = 0; frame < frames; frame++)
        {
            float sum = 0f;
            for (int channel = 0; channel < channels; channel++)
            {
                int offset = (frame * channels + channel) * bytesPerSample;
                sum += isFloat
                    ? BitConverter.ToSingle(e.Buffer, offset)
                    : isPcm16
                        ? BitConverter.ToInt16(e.Buffer, offset) / 32768f
                        : e.Buffer[offset] / 128f;
            }

            // Down-mix to mono so the visualizers always see a single channel.
            _monoBuffer[frame] = sum / channels;
        }

        DataAvailable?.Invoke(this, new AudioSamplesEventArgs(_monoBuffer, frames));
    }

    public void StartRecording() => _waveIn.StartRecording();

    public void StopRecording() => _waveIn.StopRecording();

    public void Dispose()
    {
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.Dispose();
    }
}
