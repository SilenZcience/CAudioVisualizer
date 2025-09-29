using OpenTK.Mathematics;
using AudioVisualizerC.Visualizers;

namespace AudioVisualizerC.Core;

public class VisualizerManager : IDisposable
{
    private readonly Dictionary<string, IVisualizer> _visualizers = new();

    public IReadOnlyDictionary<string, IVisualizer> Visualizers => _visualizers;

    public VisualizerManager()
    {
        InitializeVisualizers();
    }

    private void InitializeVisualizers()
    {
        // Register built-in visualizers
        RegisterVisualizer(new CircleVisualizer());
        RegisterVisualizer(new WaveformVisualizer());
        RegisterVisualizer(new ReverseWaveformVisualizer());
        RegisterVisualizer(new TriangleVisualizer());
        RegisterVisualizer(new SpectrumBarsVisualizer());
        RegisterVisualizer(new DebugInfoVisualizer());

        // Initialize all visualizers
        foreach (var visualizer in _visualizers.Values)
        {
            visualizer.Initialize();
        }
    }

    public void RegisterVisualizer(IVisualizer visualizer)
    {
        if (_visualizers.ContainsKey(visualizer.Name))
        {
            Console.WriteLine($"Warning: Visualizer '{visualizer.Name}' is already registered.");
            return;
        }

        _visualizers[visualizer.Name] = visualizer;
        visualizer.IsEnabled = true;
    }

    public void UpdateVisualizers(float[] waveformData, double deltaTime)
    {
        foreach (var visualizer in _visualizers.Values.Where(v => v.IsEnabled))
        {
            visualizer.Update(waveformData, deltaTime);
        }
    }    public void RenderVisualizers(Matrix4 projection, Vector2i windowSize)
    {
        foreach (var visualizer in _visualizers.Values.Where(v => v.IsEnabled))
        {
            visualizer.Render(projection, windowSize);
        }
    }

    public void ToggleVisualizer(string name, bool enabled)
    {
        if (_visualizers.TryGetValue(name, out var visualizer))
        {
            visualizer.IsEnabled = enabled;
        }
    }

    public void SaveVisualizerConfigurations(Dictionary<string, string> configs, List<string> enabledVisualizers)
    {
        configs.Clear();
        enabledVisualizers.Clear();

        foreach (var kvp in _visualizers)
        {
            var visualizer = kvp.Value;
            var name = kvp.Key;

            // Save enabled state
            if (visualizer.IsEnabled)
            {
                enabledVisualizers.Add(name);
            }

            // Save configuration if visualizer is configurable
            if (visualizer is IConfigurable configurable)
            {
                try
                {
                    var config = configurable.SaveConfiguration();
                    configs[name] = config;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to save configuration for {name}: {ex.Message}");
                }
            }
        }
    }

    public void LoadVisualizerConfigurations(Dictionary<string, string> configs, List<string> enabledVisualizers)
    {
        // Load enabled states
        foreach (var kvp in _visualizers)
        {
            kvp.Value.IsEnabled = enabledVisualizers.Contains(kvp.Key);
        }

        // Load configurations
        foreach (var configKvp in configs)
        {
            var name = configKvp.Key;
            var configJson = configKvp.Value;

            if (_visualizers.TryGetValue(name, out var visualizer) && visualizer is IConfigurable configurable)
            {
                try
                {
                    configurable.LoadConfiguration(configJson);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load configuration for {name}: {ex.Message}");
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var visualizer in _visualizers.Values)
        {
            visualizer.Dispose();
        }
        _visualizers.Clear();

        GC.SuppressFinalize(this);
    }
}
