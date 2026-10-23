namespace CAudioVisualizer.Core;

public sealed class AudioCaptureException : Exception
{
    public AudioCaptureException(string message) : base(message)
    {
    }
}

public sealed class AudioSamplesEventArgs : EventArgs
{
    public float[] Samples { get; }
    public int SampleCount { get; }

    public AudioSamplesEventArgs(float[] samples, int sampleCount)
    {
        Samples = samples;
        SampleCount = sampleCount;
    }
}

public interface IAudioCapture : IDisposable
{
    string DeviceName { get; }

    int SampleRate { get; }
    int Channels { get; }
    int BitsPerSample { get; }
    int AverageBytesPerSecond { get; }

    event EventHandler<AudioSamplesEventArgs>? DataAvailable;

    void StartRecording();
    void StopRecording();
}
