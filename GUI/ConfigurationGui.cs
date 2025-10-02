using ImGuiNET;
using CAudioVisualizer.Core;
using CAudioVisualizer.Configuration;
using OpenTK.Windowing.Desktop;

namespace CAudioVisualizer.GUI;

public class ConfigurationGui
{
    private readonly VisualizerManager _visualizerManager;
    private readonly AppConfig _appConfig;
    private readonly Action? _onConfigChanged;
    private readonly CAudioVisualizer.AudioVisualizerWindow? _window;

    // Audio device list - only populated when refresh is pressed
    private List<AudioDeviceInfo>? _audioDevices;
    private string[]? _deviceNames;

    // UI state for instance management
    private int _selectedTypeIndex = 0;

    public ConfigurationGui(VisualizerManager visualizerManager, AppConfig appConfig, Action? onConfigChanged = null, CAudioVisualizer.AudioVisualizerWindow? window = null)
    {
        _visualizerManager = visualizerManager;
        _appConfig = appConfig;
        _onConfigChanged = onConfigChanged;
        _window = window;
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

                    _onConfigChanged?.Invoke();
                    CAudioVisualizer.Visualizers.DebugInfoVisualizer.ResetFpsStats();
                    _window?.SwitchToMonitor(_appConfig.SelectedMonitorIndex);
                }

                if (ImGui.MenuItem("Reset to Defaults"))
                {
                    _appConfig.ResetToDefaults();

                    _onConfigChanged?.Invoke();
                    CAudioVisualizer.Visualizers.DebugInfoVisualizer.ResetFpsStats();
                    // _window?.SwitchToMonitor(_appConfig.SelectedMonitorIndex);

                    foreach (var instance in _visualizerManager.GetAllInstances().Values)
                    {
                        if (instance.Visualizer is IConfigurable configurable)
                        {
                            configurable.ResetToDefaults();
                        }
                    }

                    _visualizerManager.SaveVisualizerConfigurations(_appConfig.VisualizerConfigs, _appConfig.EnabledVisualizers);
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
            if (ImGui.SliderInt("Target FPS", ref targetFps, 16, 360))
            {
                _appConfig.TargetFPS = targetFps;
                _onConfigChanged?.Invoke();
                // Reset FPS statistics when target changes
                CAudioVisualizer.Visualizers.DebugInfoVisualizer.ResetFpsStats();
            }

            bool enableVSync = _appConfig.EnableVSync;
            if (ImGui.Checkbox("Enable VSync", ref enableVSync))
            {
                _appConfig.EnableVSync = enableVSync;
                _onConfigChanged?.Invoke();
                // Reset FPS statistics when target changes
                CAudioVisualizer.Visualizers.DebugInfoVisualizer.ResetFpsStats();
            }

            ImGui.Separator();
            ImGui.Text("Audio Settings");
            ImGui.Separator();

            if (ImGui.Button("Refresh Audio Devices"))
            {
                try
                {
                    _audioDevices = AudioDeviceManager.GetAvailableOutputDevices();
                    _deviceNames = _audioDevices.Select(d => d.Name).ToArray();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to refresh audio devices: {ex.Message}");
                    _audioDevices = null;
                    _deviceNames = null;
                }
            }

            ImGui.SameLine();
            if (_audioDevices == null)
            {
                ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1.0f),
                    "Press 'Refresh Audio Devices' to load available devices");
            }
            else
            {
                ImGui.TextColored(new System.Numerics.Vector4(0.6f, 0.6f, 0.6f, 1.0f),
                    $"Found {_deviceNames!.Length} audio device(s)");
            }

            // Audio device selection (only show if devices are loaded)
            if (_audioDevices != null && _deviceNames != null)
            {
                // Find current device index
                int currentDeviceIndex = 0;
                for (int i = 0; i < _audioDevices.Count; i++)
                {
                    if (_audioDevices[i].Id == _appConfig.SelectedAudioDeviceId)
                    {
                        currentDeviceIndex = i;
                        break;
                    }
                }

                if (ImGui.Combo("Audio Device", ref currentDeviceIndex, _deviceNames, _deviceNames.Length))
                {
                    var selectedDevice = _audioDevices[currentDeviceIndex];

                    _appConfig.SelectedAudioDeviceId = selectedDevice.Id;
                    _appConfig.SelectedAudioDeviceName = selectedDevice.Name;

                    if (_window != null)
                    {
                        _window.ChangeAudioDevice(selectedDevice.Id, selectedDevice.Name);
                    }

                    _onConfigChanged?.Invoke();
                }
            }

            // Always show current device
            ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1.0f),
                "Current: " + _appConfig.SelectedAudioDeviceName);

            ImGui.Separator();
            ImGui.Text("Display Settings");
            ImGui.Separator();

            // Monitor selection
            var monitors = Monitors.GetMonitors();
            var monitorNames = new string[monitors.Count];
            for (int i = 0; i < monitors.Count; i++)
            {
                var monitor = monitors[i];
                monitorNames[i] = $"Monitor {i + 1} ({monitor.HorizontalResolution}x{monitor.VerticalResolution})";
            }

            int currentMonitor = Math.Max(0, Math.Min(_appConfig.SelectedMonitorIndex, monitors.Count - 1));
            if (ImGui.Combo("Target Monitor", ref currentMonitor, monitorNames, monitorNames.Length))
            {
                _appConfig.SelectedMonitorIndex = currentMonitor;
                _window?.SwitchToMonitor(currentMonitor);
            }

            bool spanAllMonitors = _appConfig.SpanAllMonitors;
            if (ImGui.Checkbox("Span across all monitors", ref spanAllMonitors))
            {
                _appConfig.SpanAllMonitors = spanAllMonitors;
                _window?.SwitchToMonitor(_appConfig.SelectedMonitorIndex);
            }

            if (monitors.Count <= 1)
            {
                ImGui.SameLine();
                ImGui.TextColored(new System.Numerics.Vector4(1.0f, 1.0f, 0.0f, 1.0f), "Multi-monitor spanning requires 2+ monitors");
            }
            else if (_appConfig.SpanAllMonitors)
            {
                ImGui.SameLine();
                ImGui.TextColored(new System.Numerics.Vector4(0.0f, 1.0f, 0.0f, 1.0f), $"Spanning across {monitors.Count} monitors");
            }

            ImGui.EndTabItem();
        }
    }


    private void RenderVisualizerTabs()
    {
        // Add the "Manage Instances" tab first
        if (ImGui.BeginTabItem("Manage Instances"))
        {
            RenderInstanceManagementTab();
            ImGui.EndTabItem();
        }

        // Render a tab for each visualizer instance
        foreach (var instance in _visualizerManager.GetAllInstances().Values)
        {
            if (ImGui.BeginTabItem(instance.DisplayName))
            {
                ImGui.Text($"{instance.DisplayName} Configuration");
                ImGui.Separator();

                bool isEnabled = instance.Visualizer.IsEnabled;
                if (ImGui.Checkbox("Enabled", ref isEnabled))
                {
                    _visualizerManager.ToggleVisualizer(instance.InstanceId, isEnabled);
                }

                ImGui.Separator();

                // Render visualizer-specific configuration
                instance.Visualizer.RenderConfigGui();

                ImGui.EndTabItem();
            }
        }
    }

    private void RenderInstanceManagementTab()
    {
        ImGui.Text("Visualizer Instance Management");
        ImGui.Separator();

        // Show current instances
        ImGui.Text("Current Instances:");
        var instances = _visualizerManager.GetAllInstances();

        foreach (var instance in instances.Values)
        {
            ImGui.BulletText($"{instance.DisplayName} ({instance.TypeName}) - {(instance.Visualizer.IsEnabled ? "Enabled" : "Disabled")}");
            ImGui.SameLine();

            if (ImGui.SmallButton($"Remove##{instance.InstanceId}"))
            {
                _visualizerManager.RemoveVisualizerInstance(instance.InstanceId);

                // Save the updated configuration immediately
                if (_appConfig != null)
                {
                    _visualizerManager.SaveVisualizerConfigurations(_appConfig.VisualizerConfigs, _appConfig.EnabledVisualizers);
                    _onConfigChanged?.Invoke();
                }
                break; // Break to avoid modifying collection while iterating
            }
        }

        ImGui.Separator();
        ImGui.Text("Add New Instance:");

        // Dropdown for visualizer types
        var availableTypes = _visualizerManager.GetAvailableVisualizerTypes().ToArray();

        if (availableTypes.Length > 0)
        {
            if (ImGui.Combo("Visualizer Type", ref _selectedTypeIndex, availableTypes, availableTypes.Length))
            {
                // Selection changed, but we don't need to do anything here
            }

            if (ImGui.Button("Add Instance"))
            {
                if (_selectedTypeIndex >= 0 && _selectedTypeIndex < availableTypes.Length)
                {
                    var typeName = availableTypes[_selectedTypeIndex];
                    var newInstance = VisualizerFactory.CreateVisualizerInstance(typeName, instances.Values);

                    if (newInstance != null)
                    {
                        _visualizerManager.RegisterVisualizerInstance(newInstance);

                        // Save the updated configuration immediately
                        if (_appConfig != null)
                        {
                            _visualizerManager.SaveVisualizerConfigurations(_appConfig.VisualizerConfigs, _appConfig.EnabledVisualizers);
                            _onConfigChanged?.Invoke();
                        }
                    }
                }
            }
        }
    }
}
