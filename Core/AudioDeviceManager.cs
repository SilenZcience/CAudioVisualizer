using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CAudioVisualizer.Core;

public class AudioDeviceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DataFlow { get; set; } = "";
    public bool IsDefault { get; set; } = false;
}

public static class AudioDeviceManager
{
    public static List<AudioDeviceInfo> GetAvailableAudioDevices()
    {
        if (OperatingSystem.IsWindows())
            return GetAvailableAudioDevicesWasapi();

        return GetAvailableAudioDevicesPulse();
    }

    public static IAudioCapture? CreateAudioCapture(string deviceId)
    {
        if (OperatingSystem.IsWindows())
            return CreateAudioCaptureWasapi(deviceId);

        return PulseAudioClient.TryCreateCapture(deviceId, GetDeviceName(deviceId));
    }

    public static string GetDeviceName(string deviceId)
    {
        if (OperatingSystem.IsWindows())
            return GetDeviceNameWasapi(deviceId);

        return GetDeviceNamePulse(deviceId);
    }

    #region Windows (WASAPI)

    private static List<AudioDeviceInfo> GetAvailableAudioDevicesWasapi()
    {
        var devices = new List<AudioDeviceInfo>();

        try
        {
            var enumerator = new MMDeviceEnumerator();

            // Add default device first
            try
            {
                var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                devices.Add(new AudioDeviceInfo
                {
                    Id = defaultDevice.ID,
                    Name = $"Default - {defaultDevice.FriendlyName}",
                    IsDefault = true,
                    DataFlow = "Playback "
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get default audio device: {ex.Message}");
            }

            // Add all other render devices
            var deviceOutputCollection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in deviceOutputCollection)
            {
                // Skip if already added
                if (devices.Any(d => d.Id == device.ID))
                    continue;

                devices.Add(new AudioDeviceInfo
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    IsDefault = false,
                    DataFlow = "Playback "
                });
            }

            var deviceInputCollection = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            foreach (var device in deviceInputCollection)
            {
                // Skip if already added
                if (devices.Any(d => d.Id == device.ID))
                    continue;

                devices.Add(new AudioDeviceInfo
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    IsDefault = false,
                    DataFlow = "Recording"
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to enumerate audio devices: {ex.Message}");

            // Fallback: add a default entry
            devices.Add(CreateFallbackDevice());
        }

        return devices;
    }

    private static IAudioCapture CreateAudioCaptureWasapi(string deviceId)
    {
        try
        {
            IWaveIn waveIn;

            if (string.IsNullOrEmpty(deviceId))
            {
                // Use default device
                waveIn = new WasapiLoopbackCapture();
            }
            else
            {
                // Use specific device
                var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);
                if (device.DataFlow == DataFlow.Render)
                {
                    waveIn = new WasapiLoopbackCapture(device);
                }
                else if (device.DataFlow == DataFlow.Capture)
                {
                    waveIn = new WasapiCapture(device);
                }
                else
                {
                    throw new InvalidOperationException("Selected device is neither render nor capture.");
                }
            }

            return new NAudioCapture(waveIn, GetDeviceNameWasapi(deviceId));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create audio capture for device {deviceId}: {ex.Message}");
            Console.WriteLine("Falling back to default device...");

            // Fallback to default device
            return new NAudioCapture(new WasapiLoopbackCapture(), GetDeviceNameWasapi(""));
        }
    }

    private static string GetDeviceNameWasapi(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return "Default Device";

        try
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(deviceId);
            return device.FriendlyName;
        }
        catch (Exception)
        {
            return "Unknown Device";
        }
    }

    #endregion

    #region Linux (PulseAudio)

    private static List<AudioDeviceInfo> GetAvailableAudioDevicesPulse()
    {
        var devices = new List<AudioDeviceInfo>();

        try
        {
            PulseAudioSnapshot snapshot = PulseAudioClient.TryQuerySnapshot();

            if (snapshot.Sinks.Count == 0 && snapshot.Sources.Count == 0)
            {
                Console.WriteLine("No PulseAudio devices found.");
                devices.Add(CreateFallbackDevice());
                return devices;
            }

            // Default playback device (loopback capture of the default sink) first.
            string defaultSinkName = !string.IsNullOrEmpty(snapshot.DefaultSinkName)
                ? snapshot.DefaultSinkName
                : snapshot.Sinks.FirstOrDefault()?.Name ?? "";

            var defaultSink = snapshot.Sinks.FirstOrDefault(s => s.Name == defaultSinkName);
            devices.Add(new AudioDeviceInfo
            {
                Id = "",
                Name = defaultSink != null
                    ? $"Default - {defaultSink.Description}"
                    : "Default Device",
                IsDefault = true,
                DataFlow = "Playback "
            });

            // All other playback devices.
            foreach (var sink in snapshot.Sinks)
            {
                devices.Add(new AudioDeviceInfo
                {
                    Id = sink.Name,
                    Name = sink.Description,
                    IsDefault = sink.Name == defaultSinkName,
                    DataFlow = "Playback "
                });
            }

            // Recording devices (skip the sink monitor sources, they are already covered above).
            foreach (var source in snapshot.Sources)
            {
                if (snapshot.IsMonitorSource(source.Name))
                    continue;

                devices.Add(new AudioDeviceInfo
                {
                    Id = source.Name,
                    Name = source.Description,
                    IsDefault = source.Name == snapshot.DefaultSourceName,
                    DataFlow = "Recording"
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to enumerate audio devices: {ex.Message}");
            devices.Add(CreateFallbackDevice());
        }

        return devices;
    }

    private static string GetDeviceNamePulse(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return "Default Device";

        try
        {
            PulseAudioSnapshot snapshot = PulseAudioClient.TryQuerySnapshot();

            string description = snapshot.Sinks
                .FirstOrDefault(s => s.Name == deviceId)?.Description
                ?? snapshot.Sources
                    .FirstOrDefault(s => s.Name == deviceId || s.Name == PulseAudioSnapshot.MonitorNameFor(deviceId))?.Description
                ?? "";

            return string.IsNullOrEmpty(description) ? deviceId : description;
        }
        catch (Exception)
        {
            return "Unknown Device";
        }
    }

    #endregion

    private static AudioDeviceInfo CreateFallbackDevice()
    {
        return new AudioDeviceInfo
        {
            Id = "",
            Name = "Default Device",
            IsDefault = true,
            DataFlow = "Playback "
        };
    }
}
