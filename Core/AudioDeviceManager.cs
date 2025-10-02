using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace CAudioVisualizer.Core;

public class AudioDeviceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; } = false;
    public MMDevice? Device { get; set; }
}

public static class AudioDeviceManager
{
    public static List<AudioDeviceInfo> GetAvailableOutputDevices()
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
                    Device = defaultDevice
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get default audio device: {ex.Message}");
            }

            // Add all other render devices
            var deviceCollection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in deviceCollection)
            {
                // Skip if this is already added as default
                if (devices.Any(d => d.Id == device.ID))
                    continue;

                devices.Add(new AudioDeviceInfo
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    IsDefault = false,
                    Device = device
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to enumerate audio devices: {ex.Message}");

            // Fallback: add a default entry
            devices.Add(new AudioDeviceInfo
            {
                Id = "",
                Name = "Default Device",
                IsDefault = true,
                Device = null
            });
        }

        return devices;
    }

    public static WasapiLoopbackCapture CreateLoopbackCapture(string deviceId)
    {
        try
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                // Use default device
                return new WasapiLoopbackCapture();
            }
            else
            {
                // Use specific device
                var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);
                return new WasapiLoopbackCapture(device);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create loopback capture for device {deviceId}: {ex.Message}");
            Console.WriteLine("Falling back to default device...");

            // Fallback to default device
            return new WasapiLoopbackCapture();
        }
    }

    public static string GetDeviceName(string deviceId)
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
}
