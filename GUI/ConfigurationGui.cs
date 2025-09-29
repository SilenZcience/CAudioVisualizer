using ImGuiNET;
using CAudioVisualizer.Core;
using CAudioVisualizer.Configuration;

namespace CAudioVisualizer.GUI;

public class ConfigurationGui
{
    private readonly VisualizerManager _visualizerManager;
    private readonly AppConfig _appConfig;
    private readonly Action? _onConfigChanged;

    public ConfigurationGui(VisualizerManager visualizerManager, AppConfig appConfig, Action? onConfigChanged = null)
    {
        _visualizerManager = visualizerManager;
        _appConfig = appConfig;
        _onConfigChanged = onConfigChanged;
    }

    public void Render()
    {
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(600, 400), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Audio Visualizer Configuration", ImGuiWindowFlags.MenuBar))
        {
            RenderMenuBar();

            if (ImGui.BeginTabBar("ConfigTabs"))
            {
                RenderApplicationTab();
                RenderVisualizerTabs();

                ImGui.EndTabBar();
            }
        }
        ImGui.End();
    }

    private void RenderMenuBar()
    {
        if (ImGui.BeginMenuBar())
        {
            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Save Configuration"))
                {
                    // Save visualizer configurations before saving main config
                    _visualizerManager.SaveVisualizerConfigurations(_appConfig.VisualizerConfigs, _appConfig.EnabledVisualizers);
                    _appConfig.SaveConfiguration("config.json");
                }

                if (ImGui.MenuItem("Load Configuration"))
                {
                    _appConfig.LoadConfiguration("config.json");
                    // Load visualizer configurations after loading main config
                    _visualizerManager.LoadVisualizerConfigurations(_appConfig.VisualizerConfigs, _appConfig.EnabledVisualizers);
                }

                if (ImGui.MenuItem("Reset to Defaults"))
                {
                    _appConfig.ResetToDefaults();
                    // Apply reset to visualizers as well
                    _visualizerManager.LoadVisualizerConfigurations(_appConfig.VisualizerConfigs, _appConfig.EnabledVisualizers);
                }

                ImGui.EndMenu();
            }

            ImGui.EndMenuBar();
        }
    }

    private void RenderApplicationTab()
    {
        if (ImGui.BeginTabItem("Application"))
        {
            ImGui.Text("General Settings");
            ImGui.Separator();

            int targetFps = _appConfig.TargetFPS;
            if (ImGui.SliderInt("Target FPS", ref targetFps, 30, 360))
            {
                _appConfig.TargetFPS = targetFps;
                _onConfigChanged?.Invoke();
                // Reset FPS statistics when target changes
                CAudioVisualizer.Visualizers.DebugInfoVisualizer.ResetFpsStats();
            }
            ImGui.SameLine();
            ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1.0f), "(Applied in real-time)");

            bool enableVSync = _appConfig.EnableVSync;
            if (ImGui.Checkbox("Enable VSync", ref enableVSync))
            {
                _appConfig.EnableVSync = enableVSync;
                _onConfigChanged?.Invoke();
                // Reset FPS statistics when target changes
                CAudioVisualizer.Visualizers.DebugInfoVisualizer.ResetFpsStats();
            }
            ImGui.SameLine();
            ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1.0f), "(Applied in real-time)");

            ImGui.EndTabItem();
        }
    }



    private void RenderVisualizerTabs()
    {
        foreach (var visualizer in _visualizerManager.Visualizers.Values)
        {
            if (ImGui.BeginTabItem(visualizer.Name))
            {
                ImGui.Text($"{visualizer.Name} Configuration");
                ImGui.Separator();

                bool isEnabled = visualizer.IsEnabled;
                if (ImGui.Checkbox("Enabled", ref isEnabled))
                {
                    _visualizerManager.ToggleVisualizer(visualizer.Name, isEnabled);
                }

                ImGui.Separator();

                // Render visualizer-specific configuration
                visualizer.RenderConfigGui();

                ImGui.EndTabItem();
            }
        }
    }
}
