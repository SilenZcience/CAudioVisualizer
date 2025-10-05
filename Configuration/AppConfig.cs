using System.Text.Json;

namespace CAudioVisualizer.Configuration;

public class AppConfig
{
    public int TargetFPS { get; set; } = 60;
    public bool UnlimitedFPS { get; set; } = false;
    public bool EnableVSync { get; set; } = true;
    public int SelectedMonitorIndex { get; set; } = 0;
    public bool SpanAllMonitors { get; set; } = false;

    public string SelectedAudioDeviceId { get; set; } = "";
    public string SelectedAudioDeviceName { get; set; } = "Default Device";

    public Dictionary<string, string> VisualizerConfigs { get; set; } = new();
    public List<string> EnabledVisualizers { get; set; } = new();

    private static string GetUserConfigDirectory()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configDir = Path.Combine(appDataPath, "CAudioVisualizer");

        if (!Directory.Exists(configDir))
        {
            Directory.CreateDirectory(configDir);
        }

        return configDir;
    }

    public static string GetConfigFilePath()
    {
        return Path.Combine(GetUserConfigDirectory(), "config.json");
    }

    public static string GetImGuiConfigPath()
    {
        return Path.Combine(GetUserConfigDirectory(), "imgui.ini");
    }

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
                    TargetFPS = config.TargetFPS;
                    UnlimitedFPS = config.UnlimitedFPS;
                    EnableVSync = config.EnableVSync;
                    SelectedMonitorIndex = config.SelectedMonitorIndex;
                    SpanAllMonitors = config.SpanAllMonitors;
                    SelectedAudioDeviceId = config.SelectedAudioDeviceId ?? "";
                    SelectedAudioDeviceName = config.SelectedAudioDeviceName ?? "Default Device";
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
        TargetFPS = 60;
        UnlimitedFPS = false;
        EnableVSync = true;
        // SelectedMonitorIndex = 0;
        // SpanAllMonitors = false;
        VisualizerConfigs.Clear();
        EnabledVisualizers.Clear();
    }
}
