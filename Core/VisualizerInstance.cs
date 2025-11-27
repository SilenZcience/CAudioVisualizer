using CAudioVisualizer.Visualizers;

namespace CAudioVisualizer.Core;


public class VisualizerInstance
{
    public string InstanceId { get; }
    public string TypeName { get; }
    public string DisplayName { get; set; }
    public IVisualizer Visualizer { get; }
    public DateTime CreatedAt { get; } = DateTime.Now;

    public VisualizerInstance(string typeNam, IVisualizer visualizer, int instanceNumber = 1)
    {
        TypeName = typeNam;
        Visualizer = visualizer;
        InstanceId = $"{TypeName}_{instanceNumber}";
        DisplayName = instanceNumber == 1 ? TypeName : $"{TypeName} {instanceNumber}";
    }

    public VisualizerInstance(string instanceId, string typeName, string displayName, IVisualizer visualizer)
    {
        InstanceId = instanceId;
        TypeName = typeName;
        DisplayName = displayName;
        Visualizer = visualizer;
    }
}

public static class VisualizerFactory
{
    public static readonly Dictionary<string, string> AvailableTypes = new()
    {
        { "Triangle", "Triangle Visualizer" },
        { "Circle", "Circle Visualizer" },
        { "Waveform", "Waveform Visualizer" },
        { "Spectrum", "Spectrum Visualizer" },
        { "CustomShader", "CustomShader Visualizer" },
        { "Debug", "Debug Info Visualizer" }
    };

    public static IVisualizer? CreateVisualizer(string typeName)
    {
        return typeName switch
        {
            "Triangle" => new TriangleVisualizer(),
            "Circle" => new CircleVisualizer(),
            "Waveform" => new WaveformVisualizer(),
            "Spectrum" => new SpectrumVisualizer(),
            "CustomShader" => new CustomShaderVisualizer(),
            "Debug" => new DebugInfoVisualizer(),
            _ => null
        };
    }

    public static IEnumerable<string> GetAvailableTypes()
    {
        return AvailableTypes.Keys;
    }

    public static VisualizerInstance? CreateVisualizerInstance(string typeName, IEnumerable<VisualizerInstance> existingInstances)
    {
        var visualizer = CreateVisualizer(typeName);
        if (visualizer == null) return null;

        // Find the next available number for this type
        var existingNumbers = existingInstances
            .Where(v => v.TypeName == typeName)
            .Select(v => GetInstanceNumber(v.DisplayName, typeName))
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .ToHashSet();

        int instanceNumber = 1;
        while (existingNumbers.Contains(instanceNumber))
        {
            instanceNumber++;
        }

        return new VisualizerInstance(typeName, visualizer, instanceNumber);
    }

    private static int? GetInstanceNumber(string displayName, string typeName)
    {
        if (displayName == typeName) return 1;

        var prefix = $"{typeName} ";
        if (displayName.StartsWith(prefix))
        {
            var numberPart = displayName.Substring(prefix.Length);
            if (int.TryParse(numberPart, out int number))
            {
                return number;
            }
        }

        return null;
    }
}
