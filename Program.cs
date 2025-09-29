using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using NAudio.CoreAudioApi;
using NAudio.Wave;

using System.Diagnostics;
using AudioVisualizerC.Core;
using AudioVisualizerC.Visualizers;
using AudioVisualizerC.GUI;
using AudioVisualizerC.Configuration;

namespace AudioVisualizerC;

public class AudioVisualizerWindow : GameWindow
{
    private WasapiLoopbackCapture? _capture;
    private readonly List<float> _audioBuffer = new();
    private readonly object _bufferLock = new();
    // Audio processing constants
    private const int BUFFER_SIZE = 2048;

    // Waveform data for visualizers
    private float[] _waveformData = new float[BUFFER_SIZE];



    // New modular system components
    private VisualizerManager _visualizerManager = null!;

    // ImGui components
    private ImGuiController _imGuiController = null!;
    private ConfigurationGui _configGui = null!;
    private AppConfig _appConfig = null!;
    private bool _showConfigWindow = false;

    public AudioVisualizerWindow(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings)
    {
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        GL.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // Initialize ImGui
        _imGuiController = new ImGuiController(ClientSize.X, ClientSize.Y);

        // Initialize application configuration
        _appConfig = new AppConfig();
        _appConfig.LoadConfiguration("config.json");

        // Initialize the modular system
        _visualizerManager = new VisualizerManager();

        // Load visualizer configurations
        _visualizerManager.LoadVisualizerConfigurations(_appConfig.VisualizerConfigs, _appConfig.EnabledVisualizers);

        // Initialize configuration GUI with callback for immediate config changes
        _configGui = new ConfigurationGui(_visualizerManager, _appConfig, ApplyConfigurationSettings);

        SetupAudioCapture();



        // Apply initial configuration settings
        ApplyConfigurationSettings();
    }

    private void ApplyConfigurationSettings()
    {
        // Apply target FPS
        if (_appConfig.TargetFPS > 0)
        {
            UpdateFrequency = _appConfig.TargetFPS;
        }

        // Apply VSync setting
        VSync = _appConfig.EnableVSync ? VSyncMode.On : VSyncMode.Off;
    }

    private void SetupAudioCapture()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
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

            // Keep buffer size manageable - more efficient removal
            if (_audioBuffer.Count > BUFFER_SIZE * 2) // Reduced buffer size for lower latency
            {
                int samplesToRemove = _audioBuffer.Count - BUFFER_SIZE * 2;
                _audioBuffer.RemoveRange(0, samplesToRemove); // Remove multiple at once
            }
        }
    }

    private void ProcessAudioData()
    {
        lock (_bufferLock)
        {
            if (_audioBuffer.Count < BUFFER_SIZE) return;

            // Store waveform data for visualizers
            Array.Copy(_audioBuffer.TakeLast(BUFFER_SIZE).ToArray(), _waveformData, BUFFER_SIZE);
        }
    }

    protected override void OnRenderFrame(FrameEventArgs e)
    {
        base.OnRenderFrame(e);

        ProcessAudioData();

        // Update ImGui
        _imGuiController.Update(this, (float)e.Time);

        // Clear the screen
        GL.Clear(ClearBufferMask.ColorBufferBit);

        // Set up projection matrix for visualizers using screen pixel coordinates
        var projection = Matrix4.CreateOrthographicOffCenter(0, ClientSize.X, ClientSize.Y, 0, -1, 1);

        // Update and render all visualizers
        _visualizerManager.UpdateVisualizers(_waveformData, e.Time);
        _visualizerManager.RenderVisualizers(projection, ClientSize);

        // Render ImGui
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

        // Toggle configuration GUI with F3
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

        // Save visualizer configurations to AppConfig before saving
        if (_appConfig != null && _visualizerManager != null)
        {
            _visualizerManager.SaveVisualizerConfigurations(_appConfig.VisualizerConfigs, _appConfig.EnabledVisualizers);
            _appConfig.SaveConfiguration("config.json");
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
        // Load configuration early to get initial settings
        var tempConfig = new AudioVisualizerC.Configuration.AppConfig();
        tempConfig.LoadConfiguration("config.json");

        var gameWindowSettings = GameWindowSettings.Default;
        // Get primary monitor size for borderless fullscreen
        var primaryMonitor = Monitors.GetPrimaryMonitor();

        var nativeWindowSettings = new NativeWindowSettings()
        {
            Title = "Audio Visualizer - OpenGL 4.6",
            Flags = ContextFlags.ForwardCompatible,
            Profile = ContextProfile.Core,
            APIVersion = new Version(4, 6),
            WindowBorder = WindowBorder.Hidden,
            WindowState = WindowState.Normal,
            ClientSize = new Vector2i(primaryMonitor.HorizontalResolution, primaryMonitor.VerticalResolution),
            Location = new Vector2i(0, 0)
        };

        using var window = new AudioVisualizerWindow(gameWindowSettings, nativeWindowSettings);
        window.Run();
    }
}
