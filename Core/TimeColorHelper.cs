using OpenTK.Mathematics;

namespace CAudioVisualizer.Core;

public static class TimeColorHelper
{
    public static Vector3 GetTimeBasedColor()
    {
        // Create a time-based color that cycles through hues
        float time = (float)(DateTime.Now.TimeOfDay.TotalSeconds * 0.1); // Slow cycling

        float hue = (time % 1.0f) * 360.0f; // 0-360 degrees
        float saturation = 1.0f;
        float value = 1.0f;

        return HsvToRgb(hue, saturation, value);
    }

    public static Vector3 GetRealTimeBasedColor()
    {
        DateTime now = DateTime.Now;

        // Convert time to color components (0-1 range for OpenGL)
        // Hour (0-23) -> Red (0-1), Minute (0-59) -> Green (0-1), Second (0-59) -> Blue (0-1)
        float red = (float)now.Hour / 24.0f;
        float green = (float)now.Minute / 60.0f;
        float blue = (float)now.Second / 60.0f;

        return new Vector3(red, green, blue);
    }

    public static Vector3 HsvToRgb(float h, float s, float v)
    {
        h /= 60.0f;
        int i = (int)Math.Floor(h);
        float f = h - i;
        float p = v * (1.0f - s);
        float q = v * (1.0f - s * f);
        float t = v * (1.0f - s * (1.0f - f));

        return i switch
        {
            0 => new Vector3(v, t, p),
            1 => new Vector3(q, v, p),
            2 => new Vector3(p, v, t),
            3 => new Vector3(p, q, v),
            4 => new Vector3(t, p, v),
            _ => new Vector3(v, p, q)
        };
    }

    public static Vector3 InvertColor(Vector3 color)
    {
        return new Vector3(1.0f - color.X, 1.0f - color.Y, 1.0f - color.Z);
    }
}
