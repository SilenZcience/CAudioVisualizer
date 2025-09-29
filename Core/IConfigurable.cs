namespace CAudioVisualizer.Core;

public interface IConfigurable
{
    string SaveConfiguration();
    void LoadConfiguration(string configuration);
    void ResetToDefaults();
}
