using OpenTK.Mathematics;
using CAudioVisualizer.Visualizers;
using CAudioVisualizer.Configuration;

namespace CAudioVisualizer.Core;

public class VisualizerManager : IDisposable
{
    private readonly Dictionary<string, VisualizerInstance> _instances = new();
    private Vector2i _currentWindowSize = new Vector2i(800, 600); // Default fallback size

    public void RegisterVisualizerInstance(VisualizerInstance instance)
    {
        if (_instances.ContainsKey(instance.InstanceId))
        {
            Console.WriteLine($"Warning: Visualizer instance '{instance.InstanceId}' is already registered.");
            return;
        }

        _instances[instance.InstanceId] = instance;
        instance.Visualizer.SetVisualizerManager(this);

        // Set instance display name for visualizers that need unique identification
        if (instance.Visualizer is DebugInfoVisualizer debugVisualizer)
        {
            debugVisualizer.SetInstanceDisplayName(instance.DisplayName);
        }

        instance.Visualizer.Initialize();

        Console.WriteLine($"Registered visualizer instance: {instance.DisplayName} ({instance.InstanceId})");
    }

    public bool RemoveVisualizerInstance(string instanceId)
    {
        if (_instances.TryGetValue(instanceId, out var instance))
        {
            instance.Visualizer.Dispose();
            _instances.Remove(instanceId);
            Console.WriteLine($"Removed visualizer instance: {instance.DisplayName} ({instanceId})");
            return true;
        }
        return false;
    }

    public void UpdateVisualizers(float[] waveformData, float[] fftData, double deltaTime)
    {
        foreach (var instance in _instances.Values)
        {
            if (instance.Visualizer.IsEnabled)
            {
                instance.Visualizer.Update(waveformData, fftData, deltaTime);
            }
        }
    }

    public void RenderVisualizers(Matrix4 projection, Vector2i windowSize)
    {
        // Update current window size for all visualizers to reference
        _currentWindowSize = windowSize;

        foreach (var instance in _instances.Values)
        {
            if (instance.Visualizer.IsEnabled)
            {
                instance.Visualizer.Render(projection, windowSize);
            }
        }
    }

    public Vector2i GetCurrentWindowSize()
    {
        return _currentWindowSize;
    }

    public Dictionary<string, VisualizerInstance> GetAllInstances()
    {
        return _instances;
    }

    public IEnumerable<string> GetAvailableVisualizerTypes()
    {
        return VisualizerFactory.GetAvailableTypes();
    }

    public void ToggleVisualizer(string instanceId, bool enabled)
    {
        if (_instances.TryGetValue(instanceId, out var instance))
        {
            instance.Visualizer.IsEnabled = enabled;
        }
    }

    public void SaveVisualizerConfigurations(Dictionary<string, string> configs, List<string> enabledVisualizers)
    {
        configs.Clear();
        enabledVisualizers.Clear();

        foreach (var kvp in _instances)
        {
            var instance = kvp.Value;
            var instanceId = kvp.Key;

            if (instance.Visualizer.IsEnabled)
            {
                enabledVisualizers.Add(instanceId);
            }

            if (instance.Visualizer is IConfigurable configurable)
            {
                try
                {
                    var config = configurable.SaveConfiguration();
                    configs[instanceId] = config;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to save configuration for {instanceId}: {ex.Message}");
                }
            }
        }
    }

    public void LoadVisualizerConfigurations(Dictionary<string, string> configs, List<string> enabledVisualizers)
    {
        // Recreate instances from configuration keys
        foreach (var instanceId in configs.Keys)
        {
            if (!_instances.ContainsKey(instanceId))
            {
                var instance = CreateInstanceFromId(instanceId);
                if (instance != null)
                {
                    RegisterVisualizerInstance(instance);
                }
            }
        }

        // Also create instances for enabled visualizers that might not have configs yet
        foreach (var instanceId in enabledVisualizers)
        {
            if (!_instances.ContainsKey(instanceId))
            {
                var instance = CreateInstanceFromId(instanceId);
                if (instance != null)
                {
                    RegisterVisualizerInstance(instance);
                }
            }
        }

        foreach (var kvp in _instances)
        {
            kvp.Value.Visualizer.IsEnabled = enabledVisualizers.Contains(kvp.Key);
        }

        foreach (var configKvp in configs)
        {
            var instanceId = configKvp.Key;
            var configJson = configKvp.Value;

            if (_instances.TryGetValue(instanceId, out var instance) && instance.Visualizer is IConfigurable configurable)
            {
                try
                {
                    configurable.LoadConfiguration(configJson);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load configuration for {instanceId}: {ex.Message}");
                }
            }
        }
    }

    private VisualizerInstance? CreateInstanceFromId(string instanceId)
    {
        // Parse instance ID format: "TypeName_InstanceNumber"
        var parts = instanceId.Split('_');
        if (parts.Length < 2) return null;

        var typeName = parts[0];
        if (!int.TryParse(parts[1], out var instanceNumber)) return null;

        var visualizer = VisualizerFactory.CreateVisualizer(typeName);
        if (visualizer == null) return null;

        var displayName = instanceNumber == 1 ? typeName : $"{typeName} {instanceNumber}";
        var instance = new VisualizerInstance(instanceId, typeName, displayName, visualizer);

        // Set instance display name for visualizers that need unique identification
        if (visualizer is DebugInfoVisualizer debugVisualizer)
        {
            debugVisualizer.SetInstanceDisplayName(displayName);
        }

        return instance;
    }

    public void Dispose()
    {
        foreach (var instance in _instances.Values)
        {
            instance.Visualizer.Dispose();
        }
        _instances.Clear();

        GC.SuppressFinalize(this);
    }
}
