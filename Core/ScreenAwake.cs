using System.Diagnostics;

namespace CAudioVisualizer.Core;

public static class ScreenAwake
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private static Process? _inhibitor;

    public static void KeepAwake()
    {
        if (OperatingSystem.IsWindows())
        {
            SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED);
            return;
        }

        if (_inhibitor != null)
            return;

        // (best effort, never throws)
        try
        {
            // systemd-inhibit is available on virtually every modern Linux distribution.
            _inhibitor = StartProcess("systemd-inhibit",
                "--what=idle:sleep",
                "--why=CAudioVisualizer audio visualization",
                "--mode=block",
                "sleep", "infinity");
        }
        catch (Exception)
        {
            _inhibitor = null;
        }

        if (_inhibitor == null)
        {
            try
            {
                // Fall back to disabling DPMS/X11 screen blanking.
                StartProcess("xset", "-dpms");
                StartProcess("xset", "-s", "off");
            }
            catch (Exception)
            {
                // Not an X11 session - nothing we can do.
            }
        }
    }

    public static void AllowSleep()
    {
        if (OperatingSystem.IsWindows())
        {
            SetThreadExecutionState(ES_CONTINUOUS);
            return;
        }

        // (best effort, never throws)
        try
        {
            if (_inhibitor != null && !_inhibitor.HasExited)
            {
                _inhibitor.Kill(entireProcessTree: true);
                _inhibitor.Dispose();
            }
        }
        catch (Exception)
        {
            // Ignore, the process is gone anyway.
        }
        finally
        {
            _inhibitor = null;
        }
    }

    private static Process StartProcess(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
    }
}
