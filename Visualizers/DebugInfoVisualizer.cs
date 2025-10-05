using OpenTK.Mathematics;
using ImGuiNET;
using System.Diagnostics;
using System.Text.Json;
using CAudioVisualizer.Core;

namespace CAudioVisualizer.Visualizers;

public class DebugInfoConfig
{
    public bool Enabled { get; set; } = true;
    public Vector3 Color { get; set; } = new Vector3(1.0f, 1.0f, 1.0f);
    public Vector2 Position { get; set; } = new Vector2(10.0f, 10.0f);
    public float FontSize { get; set; } = 24.0f;
    public bool ShowFpsStats { get; set; } = true;
    public bool ShowSystemInfo { get; set; } = false;
    public bool ShowHotkeyTooltip { get; set; } = false;
}

public class DebugInfoVisualizer : IVisualizer, IConfigurable
{
    public bool IsEnabled { get; set; } = true;

    private DebugInfoConfig _config = new();
    private static Action? _resetStatsCallback;
    private readonly Stopwatch _fpsTimer = new();
    private int _frameCount = 0;
    private double _currentFps = 0;
    private double _minFps = double.MaxValue;
    private double _maxFps = 0;
    private double _avgFps = 0;
    private readonly List<double> _fpsHistory = new();
    private const int FPS_HISTORY_SIZE = 60; // Keep 1 second of history at 60fps
    private VisualizerManager? _visualizerManager;
    private string _instanceDisplayName = "Debug Info";
    private double _deltaTime = 0;

    private Vector2i CurrentWindowSize => _visualizerManager?.GetCurrentWindowSize() ?? new Vector2i(800, 600);

    public void SetVisualizerManager(VisualizerManager manager)
    {
        _visualizerManager = manager;
    }

    public void SetInstanceDisplayName(string displayName)
    {
        _instanceDisplayName = displayName;
    }

    public void Initialize()
    {
        _fpsTimer.Start();
        RegisterResetCallback(ResetStats);
    }

    public static void RegisterResetCallback(Action resetCallback)
    {
        _resetStatsCallback = resetCallback;
    }

    public static void ResetFpsStats()
    {
        _resetStatsCallback?.Invoke();
    }

    private void ResetStats()
    {
        _minFps = double.MaxValue;
        _maxFps = 0;
        _fpsHistory.Clear();
        _frameCount = 0;
        _fpsTimer.Restart();
    }

    public void Update(float[] waveformData, float[] fftData, double deltaTime)
    {
        _deltaTime = deltaTime;

        // Update FPS calculation
        _frameCount++;

        if (_fpsTimer.ElapsedMilliseconds >= 1000) // Update every second
        {
            _currentFps = _frameCount / (_fpsTimer.ElapsedMilliseconds / 1000.0);

            // Update statistics
            if (_currentFps < _minFps) _minFps = _currentFps;
            if (_currentFps > _maxFps) _maxFps = _currentFps;

            // Add to history for average calculation
            _fpsHistory.Add(_currentFps);
            if (_fpsHistory.Count > FPS_HISTORY_SIZE)
                _fpsHistory.RemoveAt(0);

            _avgFps = _fpsHistory.Average();

            // Reset counters
            _frameCount = 0;
            _fpsTimer.Restart();
        }
    }

    public void Render(Matrix4 projection)
    {
        if (!IsEnabled || !_config.Enabled) return;

        // Set up ImGui window for FPS display
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(_config.Position.X, _config.Position.Y));
        ImGui.SetNextWindowBgAlpha(0.0f); // Transparent background

        var flags = ImGuiWindowFlags.NoDecoration |
                   ImGuiWindowFlags.NoInputs |
                   ImGuiWindowFlags.NoMove |
                   ImGuiWindowFlags.NoSavedSettings |
                   ImGuiWindowFlags.AlwaysAutoResize;

        if (ImGui.Begin(_instanceDisplayName, flags))
        {
            var color = new System.Numerics.Vector4(_config.Color.X, _config.Color.Y, _config.Color.Z, 1.0f);

            ImGui.SetWindowFontScale(_config.FontSize / 16.0f);

            if (_config.ShowFpsStats)
            {
                ImGui.TextColored(color, $"FPS: {_currentFps:F1}");

                if (_fpsHistory.Count > 0)
                {
                    ImGui.TextColored(color, $"Min: {_minFps:F1} | Max: {_maxFps:F1} | Avg: {_avgFps:F1}");
                }

                if (_config.ShowSystemInfo || _config.ShowHotkeyTooltip)
                    ImGui.Separator();
            }

            if (_config.ShowSystemInfo)
            {
                try
                {
                    var process = Process.GetCurrentProcess();
                    var gcMemory = GC.GetTotalMemory(false) / (1024 * 1024);
                    var workingSet = process.WorkingSet64 / (1024 * 1024);

                    ImGui.TextColored(color, "System Info:");
                    ImGui.TextColored(color, $"CPU Cores: {Environment.ProcessorCount}");
                    ImGui.TextColored(color, $"Threads: {process.Threads.Count}");
                    ImGui.TextColored(color, $"Working Set: {workingSet} MB");
                    ImGui.TextColored(color, $"GC Memory: {gcMemory} MB");
                    ImGui.TextColored(color, $"Gen 0/1/2 GC: {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
                    ImGui.TextColored(color, $"Frame Time: {_deltaTime * 1000:F1}ms");
                }
                catch
                {
                    ImGui.TextColored(color, "System Info: Error reading data");
                }
                if (_config.ShowHotkeyTooltip)
                    ImGui.Separator();
            }

            if (_config.ShowHotkeyTooltip)
            {
                ImGui.TextColored(color, "Config Menu Hotkey: F3");
            }

            ImGui.End();
        }
    }

    public void RenderConfigGui()
    {
        var color = new System.Numerics.Vector3(_config.Color.X, _config.Color.Y, _config.Color.Z);
        if (ImGui.ColorEdit3("Text Color", ref color))
        {
            _config.Color = new Vector3(color.X, color.Y, color.Z);
        }

        ImGui.Spacing();

        ImGui.Text($"Window Size: {CurrentWindowSize.X} x {CurrentWindowSize.Y}");

        var posX = _config.Position.X;
        if (ImGui.SliderFloat("Position X", ref posX, 0.0f, CurrentWindowSize.X))
        {
            _config.Position = new Vector2(posX, _config.Position.Y);
        }

        var posY = _config.Position.Y;
        if (ImGui.SliderFloat("Position Y", ref posY, 0.0f, CurrentWindowSize.Y))
        {
            _config.Position = new Vector2(_config.Position.X, posY);
        }

        ImGui.Spacing();

        var fontSize = _config.FontSize;
        if (ImGui.SliderFloat("Font Size", ref fontSize, 8.0f, 72.0f))
        {
            _config.FontSize = fontSize;
        }

        ImGui.Spacing();

        var showFpsStats = _config.ShowFpsStats;
        if (ImGui.Checkbox("Show FPS Statistics", ref showFpsStats))
        {
            _config.ShowFpsStats = showFpsStats;
        }

        var showSystemInfo = _config.ShowSystemInfo;
        if (ImGui.Checkbox("Show System Info", ref showSystemInfo))
        {
            _config.ShowSystemInfo = showSystemInfo;
        }

        var showHotkeyTooltip = _config.ShowHotkeyTooltip;
        if (ImGui.Checkbox("Show Hotkey Tooltip", ref showHotkeyTooltip))
        {
            _config.ShowHotkeyTooltip = showHotkeyTooltip;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Reset Position"))
        {
            _config.Position = new Vector2(10.0f, 10.0f);
        }

        ImGui.SameLine();

        if (ImGui.Button("Reset Stats"))
        {
            _minFps = double.MaxValue;
            _maxFps = 0;
            _fpsHistory.Clear();
        }
    }

    public string SaveConfiguration()
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new VectorJsonConverter(), new Vector2JsonConverter() }
            };
            return JsonSerializer.Serialize(_config, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save Debug config: {ex.Message}");
            return "{}";
        }
    }

    public void LoadConfiguration(string json)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                Converters = { new VectorJsonConverter(), new Vector2JsonConverter() }
            };
            var config = JsonSerializer.Deserialize<DebugInfoConfig>(json, options);
            if (config != null)
                _config = config;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load Debug config: {ex.Message}");
        }
    }

    public void ResetToDefaults()
    {
        _config = new DebugInfoConfig();
        _minFps = double.MaxValue;
        _maxFps = 0;
        _fpsHistory.Clear();
    }

    public void Dispose()
    {
        // No unmanaged resources to dispose
    }
}
