using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Text.Json;
using ImGuiNET;
using AudioVisualizerC.Core;

namespace AudioVisualizerC.Visualizers;

public struct ReverseWaveformFrame
{
    public List<float> Vertices;
    public Vector3 Color;
    public float Brightness;
    public float LineWidth;
}

public class ReverseWaveformConfig
{
    public bool Enabled { get; set; } = true;
    public Vector3 Color { get; set; } = new Vector3(1.0f, 0.0f, 1.0f); // Magenta
    public float Amplitude { get; set; } = 1.0f; // Audio amplitude scaling
    public int PositionY { get; set; } = -1; // Y position in pixels (will be set to center on first use)
    public float LineThickness { get; set; } = 2.0f; // Line thickness
    public int StartX { get; set; } = 0; // Start X position in pixels (0 is left edge)
    public int EndX { get; set; } = -1; // End X position in pixels (will be set to right edge on first use)
    public bool UseTimeColor { get; set; } = false; // Use rainbow colors
    public bool UseRealTimeColor { get; set; } = false; // Use actual time as RGB
    public float PositionX { get; set; } = 0.5f; // X position offset (0-1, where 0.5 is center) - for overall positioning
    public bool EnableFadeTrail { get; set; } = false; // Enable fade trail effect
    public float FadeSpeed { get; set; } = 0.95f; // How fast waveforms fade (0.9 = slow, 0.99 = fast)
    public int TrailLength { get; set; } = 20; // Maximum number of trail waveforms
}

public class ReverseWaveformVisualizer : IVisualizer, IConfigurable
{
    public string Name => "Reverse Waveform";
    public string DisplayName => "Reverse Waveform";
    public bool IsEnabled { get; set; } = true;

    private ReverseWaveformConfig _config = new();
    private int _vertexBufferObject;
    private int _vertexArrayObject;
    private int _shaderProgram;
    private bool _initialized = false;
    private float[] _audioData = Array.Empty<float>();
    private Vector2i _currentWindowSize = new Vector2i(800, 600);
    private List<ReverseWaveformFrame> _trailFrames = new();

    private const string VertexShaderSource = @"
#version 330 core

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aColor;

uniform mat4 projection;

out vec3 vertexColor;

void main()
{
    gl_Position = projection * vec4(aPosition, 1.0);
    vertexColor = aColor;
}";

    private const string FragmentShaderSource = @"
#version 330 core

in vec3 vertexColor;
out vec4 FragColor;

void main()
{
    FragColor = vec4(vertexColor, 1.0);
}";

    public void Initialize()
    {
        if (_initialized) return;

        // Create and compile shaders
        int vertexShader = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vertexShader, VertexShaderSource);
        GL.CompileShader(vertexShader);

        int fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fragmentShader, FragmentShaderSource);
        GL.CompileShader(fragmentShader);

        // Create shader program
        _shaderProgram = GL.CreateProgram();
        GL.AttachShader(_shaderProgram, vertexShader);
        GL.AttachShader(_shaderProgram, fragmentShader);
        GL.LinkProgram(_shaderProgram);

        // Clean up shader objects
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);

        // Generate buffers
        _vertexArrayObject = GL.GenVertexArray();
        _vertexBufferObject = GL.GenBuffer();

        GL.BindVertexArray(_vertexArrayObject);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);

        // Position attribute
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        // Color attribute
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);

        _initialized = true;
    }

    public void Update(float[] waveformData, double deltaTime)
    {
        _audioData = waveformData;
    }

    public void Render(Matrix4 projection, Vector2i windowSize)
    {
        if (!IsEnabled || !_initialized) return;

        // Update current window size for config GUI
        _currentWindowSize = windowSize;

        // Initialize positions if not set yet
        if (_config.PositionY == -1)
            _config.PositionY = windowSize.Y / 2;
        if (_config.EndX == -1)
            _config.EndX = windowSize.X;

        GL.UseProgram(_shaderProgram);

        // Set uniforms
        int projectionLocation = GL.GetUniformLocation(_shaderProgram, "projection");
        GL.UniformMatrix4(projectionLocation, false, ref projection);

        if (_config.EnableFadeTrail)
        {
            // Update trail frames - fade existing ones and add new one
            UpdateTrailFrames(windowSize);

            // Render all trail frames
            RenderTrailFrames();
        }
        else
        {
            // Generate current waveform
            var currentWaveform = GenerateCurrentWaveform(windowSize);
            if (currentWaveform.Vertices.Count == 0) return;

            // Upload vertex data and render
            RenderWaveform(currentWaveform.Vertices, currentWaveform.LineWidth);
        }
    }



    private void UpdateTrailFrames(Vector2i windowSize)
    {
        // Generate current waveform
        var currentWaveform = GenerateCurrentWaveform(windowSize);

        // Add current waveform to trail
        _trailFrames.Insert(0, currentWaveform);

        // Update brightness of existing frames and remove old ones
        for (int i = _trailFrames.Count - 1; i >= 0; i--)
        {
            var frame = _trailFrames[i];
            frame.Brightness *= _config.FadeSpeed;

            // Remove frames that are too dim or exceed trail length
            if (frame.Brightness < 0.01f || i >= _config.TrailLength)
            {
                _trailFrames.RemoveAt(i);
            }
            else
            {
                _trailFrames[i] = frame;
            }
        }
    }

    private ReverseWaveformFrame GenerateCurrentWaveform(Vector2i windowSize)
    {
        var vertices = new List<float>();

        if (_audioData.Length == 0)
        {
            return new ReverseWaveformFrame
            {
                Vertices = vertices,
                Color = _config.Color,
                Brightness = 1.0f,
                LineWidth = _config.LineThickness
            };
        }

        // Calculate vertical position based on PositionY property
        float centerY = _config.PositionY;

        // Calculate X-axis boundaries
        float startPixel = _config.StartX;
        float endPixel = _config.EndX;

        // Ensure valid range
        if (startPixel >= endPixel || startPixel < 0 || endPixel > windowSize.X)
        {
            return new ReverseWaveformFrame
            {
                Vertices = vertices,
                Color = _config.Color,
                Brightness = 1.0f,
                LineWidth = _config.LineThickness
            };
        }

        int waveformWidth = (int)(endPixel - startPixel);

        // Use one point per pixel like GDI+ version - no artificial limits
        if (waveformWidth < 2)
        {
            return new ReverseWaveformFrame
            {
                Vertices = vertices,
                Color = _config.Color,
                Brightness = 1.0f,
                LineWidth = _config.LineThickness
            };
        }

        // Get current color
        Vector3 color = _config.UseTimeColor ? TimeColorHelper.GetTimeBasedColor() :
                       _config.UseRealTimeColor ? TimeColorHelper.GetRealTimeBasedColor() : _config.Color;

        // Generate waveform points - one per pixel but reversed (right-to-left)
        for (int x = 0; x < waveformWidth; x++)
        {
            // Reverse the X coordinate for right-to-left rendering
            int reversedX = waveformWidth - 1 - x;

            // Calculate sample index using the reversed X coordinate
            float exactIndex = reversedX * (_audioData.Length / (float)waveformWidth);
            int sampleIndex = (int)exactIndex;

            // Scale sample and calculate Y position
            float scaledSample = _audioData[sampleIndex] * _config.Amplitude;
            float y = centerY - scaledSample * (windowSize.Y * 0.4f); // Use 40% of height for waveform range

            // Calculate X position - direct pixel mapping like GDI+
            float pixelX = startPixel + x;

            // Add vertex (position + color)
            vertices.Add(pixelX);           // X
            vertices.Add(y);                // Y
            vertices.Add(0.0f);             // Z
            vertices.Add(color.X);          // R
            vertices.Add(color.Y);          // G
            vertices.Add(color.Z);          // B
        }

        return new ReverseWaveformFrame
        {
            Vertices = vertices,
            Color = color,
            Brightness = 1.0f,
            LineWidth = _config.LineThickness
        };
    }

    private void RenderTrailFrames()
    {
        GL.BindVertexArray(_vertexArrayObject);

        // Render trail frames from oldest to newest (so newest appears on top)
        for (int i = _trailFrames.Count - 1; i >= 0; i--)
        {
            var frame = _trailFrames[i];
            if (frame.Vertices.Count == 0) continue;

            // Apply brightness to vertices
            var fadedVertices = ApplyBrightnessToVertices(frame.Vertices, frame.Brightness);

            RenderWaveform(fadedVertices, frame.LineWidth);
        }
    }

    private List<float> ApplyBrightnessToVertices(List<float> vertices, float brightness)
    {
        var fadedVertices = new List<float>(vertices);

        // Apply brightness to color components (every 4th, 5th, and 6th float are RGB)
        for (int i = 3; i < fadedVertices.Count; i += 6)
        {
            fadedVertices[i] *= brightness;     // R
            fadedVertices[i + 1] *= brightness; // G
            fadedVertices[i + 2] *= brightness; // B
        }

        return fadedVertices;
    }

    private void RenderWaveform(List<float> vertices, float lineWidth)
    {
        if (vertices.Count == 0) return;

        // Upload vertex data
        GL.BindVertexArray(_vertexArrayObject);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float), vertices.ToArray(), BufferUsageHint.DynamicDraw);

        // Set line width
        GL.LineWidth(lineWidth);

        // Enable line smoothing for smoother, less blocky appearance
        GL.Enable(EnableCap.LineSmooth);
        GL.Hint(HintTarget.LineSmoothHint, HintMode.Nicest);

        // Draw waveform as line strip
        GL.DrawArrays(PrimitiveType.LineStrip, 0, vertices.Count / 6); // 6 floats per vertex (3 pos + 3 color)

        // Reset line width and disable smoothing
        GL.LineWidth(1.0f);
        GL.Disable(EnableCap.LineSmooth);
    }

    public void RenderConfigGui()
    {
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Reverse Waveform Settings");
        ImGui.Separator();

        float amplitude = _config.Amplitude;
        if (ImGui.SliderFloat("Amplitude", ref amplitude, 0.1f, 5.0f))
            _config.Amplitude = amplitude;

        float lineThickness = _config.LineThickness;
        if (ImGui.SliderFloat("Line Thickness", ref lineThickness, 1.0f, 10.0f))
            _config.LineThickness = lineThickness;

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Position");
        ImGui.Separator();

        int posY = _config.PositionY;
        if (ImGui.DragInt("Position Y", ref posY, 1.0f, 0, _currentWindowSize.Y))
        {
            _config.PositionY = posY;
        }

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Waveform Range");
        ImGui.Separator();

        int startX = _config.StartX;
        if (ImGui.DragInt("Start X", ref startX, 1.0f, 0, _currentWindowSize.X))
        {
            _config.StartX = startX;
            // Ensure StartX doesn't exceed EndX
            if (_config.StartX >= _config.EndX)
                _config.EndX = Math.Min(_currentWindowSize.X, _config.StartX + 10);
        }

        int endX = _config.EndX;
        if (ImGui.DragInt("End X", ref endX, 1.0f, 0, _currentWindowSize.X))
        {
            _config.EndX = endX;
            // Ensure EndX doesn't go below StartX
            if (_config.EndX <= _config.StartX)
                _config.StartX = Math.Max(0, _config.EndX - 10);
        }

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Colors");
        ImGui.Separator();

        bool useTimeColor = _config.UseTimeColor;
        if (ImGui.Checkbox("Rainbow Colors", ref useTimeColor))
        {
            _config.UseTimeColor = useTimeColor;
            if (useTimeColor) _config.UseRealTimeColor = false; // Disable other color mode
        }

        bool useRealTimeColor = _config.UseRealTimeColor;
        if (ImGui.Checkbox("Real Time Colors", ref useRealTimeColor))
        {
            _config.UseRealTimeColor = useRealTimeColor;
            if (useRealTimeColor) _config.UseTimeColor = false; // Disable other color mode
        }

        if (!_config.UseTimeColor && !_config.UseRealTimeColor)
        {
            var color = new System.Numerics.Vector3(_config.Color.X, _config.Color.Y, _config.Color.Z);
            if (ImGui.ColorEdit3("Color", ref color))
            {
                _config.Color = new Vector3(color.X, color.Y, color.Z);
            }
        }

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Fade Trail");
        ImGui.Separator();

        bool enableFadeTrail = _config.EnableFadeTrail;
        if (ImGui.Checkbox("Enable Fade Trail", ref enableFadeTrail))
            _config.EnableFadeTrail = enableFadeTrail;

        if (_config.EnableFadeTrail)
        {
            float fadeSpeed = _config.FadeSpeed;
            if (ImGui.SliderFloat("Fade Speed", ref fadeSpeed, 0.8f, 0.99f))
                _config.FadeSpeed = fadeSpeed;

            int trailLength = _config.TrailLength;
            if (ImGui.SliderInt("Trail Length", ref trailLength, 5, 50))
                _config.TrailLength = trailLength;
        }
    }

    public void LoadConfiguration(string json)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                Converters = { new Vector3JsonConverter() }
            };
            var config = JsonSerializer.Deserialize<ReverseWaveformConfig>(json, options);
            if (config != null)
            {
                _config = config;
                IsEnabled = _config.Enabled;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load {Name} config: {ex.Message}");
        }
    }

    public string SaveConfiguration()
    {
        try
        {
            _config.Enabled = IsEnabled;
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new Vector3JsonConverter() }
            };
            return JsonSerializer.Serialize(_config, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save {Name} config: {ex.Message}");
            return "{}";
        }
    }

    public void ResetToDefaults()
    {
        _config = new ReverseWaveformConfig();
        // Set positions to current window defaults
        _config.PositionY = _currentWindowSize.Y / 2;
        _config.EndX = _currentWindowSize.X;
        IsEnabled = _config.Enabled;
    }

    public void Dispose()
    {
        if (!_initialized) return;

        GL.DeleteBuffer(_vertexBufferObject);
        GL.DeleteVertexArray(_vertexArrayObject);
        GL.DeleteProgram(_shaderProgram);
        _initialized = false;
    }
}
