using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using NAudio.Wave;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

using CAudioVisualizer.Core;
using CAudioVisualizer.GUI;
using CAudioVisualizer.Configuration;

namespace CAudioVisualizer;

public class AudioVisualizerWindow : GameWindow
{
    private WasapiLoopbackCapture? _capture;
    private readonly List<float> _audioBuffer = new();
    private readonly object _bufferLock = new();
    private const int BUFFER_SIZE = 2048;

    private float[] _waveformData = new float[BUFFER_SIZE];
    private Complex32[] _fftBuffer = new Complex32[BUFFER_SIZE];
    private float[] _fftData = new float[BUFFER_SIZE / 2]; // Only need first half of FFT

    private VisualizerManager _visualizerManager = null!;

    private ImGuiController _imGuiController = null!;
    private ConfigurationGui _configGui = null!;
    private AppConfig _appConfig = null!;
    private bool _showConfigWindow = false;

    public AudioVisualizerWindow(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings)
    {
    }

    public void SwitchToMonitor(int monitorIndex)
    {
        var monitors = Monitors.GetMonitors();

        if (_appConfig?.SpanAllMonitors == true && monitors.Count > 1)
        {
            // Calculate full desktop bounds including taskbar areas
            int minX = monitors.Min(m => m.ClientArea.Min.X);
            int minY = monitors.Min(m => m.ClientArea.Min.Y);
            int maxX = monitors.Max(m => m.ClientArea.Max.X);
            int maxY = monitors.Max(m => m.ClientArea.Max.Y);

            Location = new Vector2i(minX, minY);
            ClientSize = new Vector2i(maxX - minX, maxY - minY);

            GL.Viewport(0, 0, ClientSize.X, ClientSize.Y);
            _imGuiController?.WindowResized(ClientSize.X, ClientSize.Y);
        }
        else if (monitorIndex >= 0 && monitorIndex < monitors.Count)
        {
            var selectedMonitor = monitors[monitorIndex];
            Location = new Vector2i(selectedMonitor.WorkArea.Min.X, selectedMonitor.WorkArea.Min.Y);
            ClientSize = new Vector2i(selectedMonitor.HorizontalResolution, selectedMonitor.VerticalResolution);

            GL.Viewport(0, 0, ClientSize.X, ClientSize.Y);
            _imGuiController?.WindowResized(ClientSize.X, ClientSize.Y);
        }
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        GL.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _imGuiController = new ImGuiController(ClientSize.X, ClientSize.Y);

        _appConfig = new AppConfig();
        _appConfig.LoadConfiguration(AppConfig.GetConfigFilePath());

        _visualizerManager = new VisualizerManager();

        _visualizerManager.LoadVisualizerConfigurations(_appConfig.VisualizerConfigs, _appConfig.EnabledVisualizers);

        _configGui = new ConfigurationGui(_visualizerManager, _appConfig, ApplyConfigurationSettings, this);

        SetupAudioCapture();

        ApplyConfigurationSettings();
    }

    private void ApplyConfigurationSettings()
    {
        UpdateFrequency = _appConfig.TargetFPS;
        VSync = _appConfig.EnableVSync ? VSyncMode.On : VSyncMode.Off;
    }

    private void SetupAudioCapture()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();

            Console.WriteLine($"Sample Rate: {_capture.WaveFormat.SampleRate} Hz");
            Console.WriteLine($"Channels: {_capture.WaveFormat.Channels}");
            Console.WriteLine($"Bits Per Sample: {_capture.WaveFormat.BitsPerSample}");
            Console.WriteLine($"Bytes Per Second: {_capture.WaveFormat.AverageBytesPerSecond}");

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += (s, e) => Console.WriteLine("Recording stopped");
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to initialize audio capture: {ex.Message}");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_bufferLock)
        {
            // Convert byte array to float array more efficiently
            int sampleCount = e.BytesRecorded / 4; // 4 bytes per float sample

            for (int i = 0; i < sampleCount; i++)
            {
                float sample = BitConverter.ToSingle(e.Buffer, i * 4);
                _audioBuffer.Add(sample);
            }

            if (_audioBuffer.Count > BUFFER_SIZE * 2)
            {
                int samplesToRemove = _audioBuffer.Count - BUFFER_SIZE * 2;
                _audioBuffer.RemoveRange(0, samplesToRemove);
            }
        }
    }

    private void ProcessAudioData()
    {
        lock (_bufferLock)
        {
            if (_audioBuffer.Count < BUFFER_SIZE) return;
            Array.Copy(_audioBuffer.TakeLast(BUFFER_SIZE).ToArray(), _waveformData, BUFFER_SIZE);
        }

        // Calculate FFT from waveform data
        ProcessFFT();
    }

    private void ProcessFFT()
    {
        // Prepare FFT buffer
        for (int i = 0; i < BUFFER_SIZE; i++)
        {
            _fftBuffer[i] = new Complex32(_waveformData[i], 0);
        }

        // Perform FFT
        Fourier.Forward(_fftBuffer, FourierOptions.Matlab);

        // Extract magnitudes for first half (avoid mirroring)
        int spectrumSize = BUFFER_SIZE / 2;
        for (int i = 0; i < spectrumSize; i++)
        {
            _fftData[i] = _fftBuffer[i].Magnitude;
        }
    }

    protected override void OnRenderFrame(FrameEventArgs e)
    {
        base.OnRenderFrame(e);

        ProcessAudioData();

        _imGuiController.Update(this, (float)e.Time);

        GL.Clear(ClearBufferMask.ColorBufferBit);

        var projection = Matrix4.CreateOrthographicOffCenter(0, ClientSize.X, ClientSize.Y, 0, -1, 1);

        // Update and render all visualizers
        _visualizerManager.UpdateVisualizers(_waveformData, _fftData, e.Time);
        _visualizerManager.RenderVisualizers(projection, ClientSize);

        if (_showConfigWindow)
        {
            _configGui.Render();
        }

        _imGuiController.Render();

        SwapBuffers();
    }

    protected override void OnUpdateFrame(FrameEventArgs e)
    {
        base.OnUpdateFrame(e);

        if (KeyboardState.IsKeyPressed(Keys.Escape))
        {
            Close();
        }

        if (KeyboardState.IsKeyPressed(Keys.F3))
        {
            _showConfigWindow = !_showConfigWindow;
        }
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, ClientSize.X, ClientSize.Y);
        _imGuiController?.WindowResized(ClientSize.X, ClientSize.Y);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        _imGuiController?.PressChar((char)e.Unicode);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _imGuiController?.MouseScroll(e.Offset);
    }

    protected override void OnUnload()
    {
        base.OnUnload();

        // Force ImGui to save its settings before shutdown
        ImGuiNET.ImGui.SaveIniSettingsToDisk(CAudioVisualizer.Configuration.AppConfig.GetImGuiConfigPath());

        if (_appConfig != null && _visualizerManager != null)
        {
            _visualizerManager.SaveVisualizerConfigurations(_appConfig.VisualizerConfigs, _appConfig.EnabledVisualizers);
            _appConfig.SaveConfiguration(CAudioVisualizer.Configuration.AppConfig.GetConfigFilePath());
        }

        _capture?.StopRecording();
        _capture?.Dispose();

        _visualizerManager?.Dispose();
        _imGuiController?.Dispose();
    }
}

static class Program
{
    static void Main()
    {
        // Load configuration early to get monitor selection
        var tempConfig = new CAudioVisualizer.Configuration.AppConfig();
        tempConfig.LoadConfiguration(CAudioVisualizer.Configuration.AppConfig.GetConfigFilePath());

        var gameWindowSettings = GameWindowSettings.Default;

        // Get all monitors
        var monitors = Monitors.GetMonitors();

        NativeWindowSettings nativeWindowSettings;

        if (tempConfig.SpanAllMonitors && monitors.Count > 1)
        {
            // Calculate full desktop bounds including taskbar areas
            int minX = 0; // Start from screen origin
            int minY = 0;
            int maxX = monitors.Max(m => m.ClientArea.Max.X);
            int maxY = monitors.Max(m => m.ClientArea.Max.Y);

            nativeWindowSettings = new NativeWindowSettings()
            {
                Title = "Audio Visualizer - Made by Silas Kraume (Multi-Monitor)",
                Flags = ContextFlags.ForwardCompatible,
                Profile = ContextProfile.Core,
                APIVersion = new Version(4, 6),
                WindowBorder = WindowBorder.Hidden,
                WindowState = WindowState.Normal,
                ClientSize = new Vector2i(maxX - minX, maxY - minY),
                Location = new Vector2i(minX, minY)
            };
        }
        else
        {
            // Single monitor mode (existing behavior)
            var selectedMonitor = monitors.Count > tempConfig.SelectedMonitorIndex && tempConfig.SelectedMonitorIndex >= 0
                ? monitors[tempConfig.SelectedMonitorIndex]
                : Monitors.GetPrimaryMonitor();

            nativeWindowSettings = new NativeWindowSettings()
            {
                Title = "Audio Visualizer - Made by Silas Kraume",
                Flags = ContextFlags.ForwardCompatible,
                Profile = ContextProfile.Core,
                APIVersion = new Version(4, 6),
                WindowBorder = WindowBorder.Hidden,
                WindowState = WindowState.Normal,
                ClientSize = new Vector2i(selectedMonitor.HorizontalResolution, selectedMonitor.VerticalResolution),
                Location = new Vector2i(selectedMonitor.WorkArea.Min.X, selectedMonitor.WorkArea.Min.Y)
            };
        }

        using var window = new AudioVisualizerWindow(gameWindowSettings, nativeWindowSettings);
        window.Run();
    }
}
