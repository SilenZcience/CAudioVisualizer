using System.Text.Json;

namespace AudioVisualizerC.Configuration;

public class AppConfig
{
    public bool ShowFPS { get; set; } = true;
    public int TargetFPS { get; set; } = 60;
    public bool EnableVSync { get; set; } = true;

    // Visualizer settings
    public Dictionary<string, string> VisualizerConfigs { get; set; } = new();
    public List<string> EnabledVisualizers { get; set; } = new();

    public void SaveConfiguration(string filePath)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }

    public void LoadConfiguration(string filePath)
    {
        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config != null)
                {
                    ShowFPS = config.ShowFPS;
                    TargetFPS = config.TargetFPS;
                    EnableVSync = config.EnableVSync;
                    VisualizerConfigs = config.VisualizerConfigs ?? new();
                    EnabledVisualizers = config.EnabledVisualizers ?? new();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load configuration: {ex.Message}");
            }
        }
    }

    public void ResetToDefaults()
    {
        ShowFPS = true;
        TargetFPS = 60;
        EnableVSync = true;
        VisualizerConfigs.Clear();
        EnabledVisualizers.Clear();
    }
}
