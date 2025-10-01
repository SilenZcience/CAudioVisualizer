using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Text.Json;
using ImGuiNET;
using CAudioVisualizer.Core;

namespace CAudioVisualizer.Visualizers;

public struct TriangleFrame
{
    public Vector3[] Vertices;
    public Vector3 Color;
    public float Brightness;
    public bool Filled;
    public float LineWidth;
}

public class TriangleConfig
{
    public bool Enabled { get; set; } = true;
    public Vector3 Color { get; set; } = new Vector3(0.0f, 1.0f, 1.0f); // Cyan
    public float BaseSize { get; set; } = 50.0f; // Base triangle size (matching GDI+ version)
    public float Amplitude { get; set; } = 300.0f; // How much audio affects size (matching GDI+ Amplitude)
    public int RotationSpeed { get; set; } = 0; // Degrees per frame (matching GDI+ default)
    public int CurrentAngle { get; set; } = 0; // Manual angle setting (matching GDI+)
    public bool Filled { get; set; } = false; // Fill triangle or just outline
    public float LineThickness { get; set; } = 2.0f; // Line thickness (matching GDI+ LineThickness)
    public float Sensitivity { get; set; } = 3.0f; // Audio sensitivity (matching GDI+ default)
    public bool UseTimeColor { get; set; } = false; // Use rainbow colors
    public bool UseRealTimeColor { get; set; } = false; // Use actual time as RGB (hour=red, minute=green, second=blue)
    public bool EnableFadeTrail { get; set; } = false; // Enable fade trail effect
    public float FadeSpeed { get; set; } = 0.95f; // How fast triangles fade (0.9 = slow, 0.99 = fast)
    public int TrailLength { get; set; } = 20; // Maximum number of trail triangles
    public int PositionX { get; set; } = -1; // X position in pixels (will be set to center on first use)
    public int PositionY { get; set; } = -1; // Y position in pixels (will be set to center on first use)
    public bool UseFFT { get; set; } = false; // Use FFT data instead of waveform data
}

public class TriangleVisualizer : IVisualizer, IConfigurable
{
    public string Name => "Triangle";
    public string DisplayName => "Triangle";
    public bool IsEnabled { get; set; } = true;

    private TriangleConfig _config = new();
    private int _vertexBufferObject;
    private int _vertexArrayObject;
    private int _shaderProgram;
    private bool _initialized = false;
    private float _currentRotation = 0.0f;
    private float[] _audioData = Array.Empty<float>();
    private float[] _fftData = Array.Empty<float>();
    private List<TriangleFrame> _trailFrames = new();
    private Vector2i _currentWindowSize = new Vector2i(800, 600);

    public void Initialize()
    {
        if (_initialized) return;

        SetupShaders();
        SetupVertexData();
        _initialized = true;
    }

    private void SetupShaders()
    {
        string vertexShaderSource = @"
            #version 460 core
            layout (location = 0) in vec3 aPosition;
            layout (location = 1) in vec3 aColor;

            uniform mat4 projection;
            out vec3 vertexColor;

            void main()
            {
                gl_Position = projection * vec4(aPosition, 1.0);
                vertexColor = aColor;
            }";

        string fragmentShaderSource = @"
            #version 460 core
            in vec3 vertexColor;
            out vec4 FragColor;

            void main()
            {
                FragColor = vec4(vertexColor, 1.0);
            }";

        int vertexShader = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vertexShader, vertexShaderSource);
        GL.CompileShader(vertexShader);

        int fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fragmentShader, fragmentShaderSource);
        GL.CompileShader(fragmentShader);

        _shaderProgram = GL.CreateProgram();
        GL.AttachShader(_shaderProgram, vertexShader);
        GL.AttachShader(_shaderProgram, fragmentShader);
        GL.LinkProgram(_shaderProgram);

        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
    }

    private void SetupVertexData()
    {
        _vertexArrayObject = GL.GenVertexArray();
        GL.BindVertexArray(_vertexArrayObject);

        _vertexBufferObject = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);

        // Position attribute
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        // Color attribute
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
    }

    public void Update(float[] waveformData, float[] fftData, double deltaTime)
    {
        _audioData = waveformData;
        _fftData = fftData;

        // Update rotation based on speed (degrees per frame, matching GDI+ behavior)
        _currentRotation += _config.RotationSpeed;
        if (_currentRotation >= 360.0f) _currentRotation -= 360.0f;
        if (_currentRotation < 0.0f) _currentRotation += 360.0f;
    }

    public void Render(Matrix4 projection, Vector2i windowSize)
    {
        if (!IsEnabled || !_initialized) return;

        // Update current window size for config GUI
        _currentWindowSize = windowSize;

        // Initialize position to center if not set yet
        if (_config.PositionX == -1)
            _config.PositionX = windowSize.X / 2;
        if (_config.PositionY == -1)
            _config.PositionY = windowSize.Y / 2;

        GL.UseProgram(_shaderProgram);

        // Set uniforms
        int projectionLocation = GL.GetUniformLocation(_shaderProgram, "projection");
        GL.UniformMatrix4(projectionLocation, false, ref projection);

        if (_config.EnableFadeTrail)
        {
            UpdateTrailFrames(windowSize);
            RenderTrailFrames();
        }
        else
        {
            // Generate current triangle vertices
            var vertices = GenerateTriangleVertices(windowSize);

            if (vertices.Count == 0) return;

            // Upload vertex data
            GL.BindVertexArray(_vertexArrayObject);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float), vertices.ToArray(), BufferUsageHint.DynamicDraw);

            // Draw triangle
            if (_config.Filled)
            {
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }
            else
            {
                GL.LineWidth(_config.LineThickness);
                GL.DrawArrays(PrimitiveType.LineLoop, 0, 3);
            }
        }
    }

    private void UpdateTrailFrames(Vector2i windowSize)
    {
        // Generate current triangle
        var currentTriangle = GenerateCurrentTriangle(windowSize);

        // Add current triangle to trail
        _trailFrames.Insert(0, currentTriangle);

        // Fade existing frames and remove completely faded ones
        for (int i = _trailFrames.Count - 1; i >= 0; i--)
        {
            var frame = _trailFrames[i];
            frame.Brightness *= _config.FadeSpeed;

            // Remove triangles that are too dim or exceed trail length
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

    private void RenderTrailFrames()
    {
        GL.BindVertexArray(_vertexArrayObject);

        // Draw triangles in reverse order (oldest first, newest last)
        for (int i = _trailFrames.Count - 1; i >= 0; i--)
        {
            var frame = _trailFrames[i];

            // Create vertex data with faded color
            var vertices = new List<float>();
            Vector3 fadedColor = frame.Color * frame.Brightness;

            for (int j = 0; j < 3; j++)
            {
                vertices.AddRange(new[] {
                    frame.Vertices[j].X, frame.Vertices[j].Y, frame.Vertices[j].Z,
                    fadedColor.X, fadedColor.Y, fadedColor.Z
                });
            }

            // Upload and draw
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float), vertices.ToArray(), BufferUsageHint.DynamicDraw);

            if (frame.Filled)
            {
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }
            else
            {
                GL.LineWidth(frame.LineWidth);
                GL.DrawArrays(PrimitiveType.LineLoop, 0, 3);
            }
        }
    }

    private List<float> GenerateTriangleVertices(Vector2i windowSize)
    {
        // Generate triangle frame and convert to vertex list
        var triangleFrame = GenerateCurrentTriangle(windowSize);
        var vertices = new List<float>();

        for (int i = 0; i < 3; i++)
        {
            // Add vertex: position (x, y, z) + color (r, g, b)
            vertices.AddRange(new[] {
                triangleFrame.Vertices[i].X, triangleFrame.Vertices[i].Y, triangleFrame.Vertices[i].Z,
                triangleFrame.Color.X, triangleFrame.Color.Y, triangleFrame.Color.Z
            });
        }

        return vertices;
    }

    private TriangleFrame GenerateCurrentTriangle(Vector2i windowSize)
    {
        // Calculate position based on configuration
        int centerX = _config.PositionX;
        int centerY = _config.PositionY;

        // Select data source based on configuration
        float[] dataSource = _config.UseFFT ? _fftData : _audioData;

        // Calculate the RMS (Root Mean Square) for better amplitude detection - exactly like GDI+ version
        float rms = 0.0f;
        if (dataSource.Length > 0)
        {
            float sum = 0.0f;
            for (int i = 0; i < dataSource.Length; i++)
            {
                sum += dataSource[i] * dataSource[i];
            }
            rms = (float)Math.Sqrt(sum / dataSource.Length);
        }

        // Apply sensitivity and power scaling for more dramatic size changes - exactly like GDI+ version
        float amplifiedRms = (float)Math.Pow(rms * _config.Sensitivity, 1.5); // Power scaling for more dramatic effect

        // Calculate triangle size with more dramatic scaling - exactly like GDI+ version
        float triangleSize = _config.BaseSize + (amplifiedRms * _config.Amplitude);

        // Ensure minimum size - exactly like GDI+ version
        triangleSize = Math.Max(triangleSize, _config.BaseSize * 0.3f);

        // Combine manual angle with automatic rotation - exactly like GDI+ version
        float totalRotation = _config.CurrentAngle + _currentRotation;

        // Get current color
        Vector3 color = _config.UseTimeColor ? TimeColorHelper.GetTimeBasedColor() :
                       _config.UseRealTimeColor ? TimeColorHelper.GetRealTimeBasedColor() : _config.Color;

        // Generate triangle vertices
        Vector3[] vertices = new Vector3[3];
        for (int i = 0; i < 3; i++)
        {
            // Each point is 120 degrees apart (360/3 = 120) - exactly like GDI+ version
            float angle = (i * 120 + totalRotation) * (float)(Math.PI / 180.0); // Convert to radians
            vertices[i] = new Vector3(
                centerX + (float)(Math.Cos(angle) * triangleSize),
                centerY + (float)(Math.Sin(angle) * triangleSize),
                0.0f
            );
        }

        return new TriangleFrame
        {
            Vertices = vertices,
            Color = color,
            Brightness = 1.0f,
            Filled = _config.Filled,
            LineWidth = _config.LineThickness
        };
    }

    public void RenderConfigGui()
    {
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Triangle Settings");
        ImGui.Separator();

        float baseSize = _config.BaseSize;
        if (ImGui.SliderFloat("Base Size", ref baseSize, 10.0f, 300.0f))
            _config.BaseSize = baseSize;

        float amplitude = _config.Amplitude;
        if (ImGui.SliderFloat("Amplitude", ref amplitude, 100.0f, 2000.0f))
            _config.Amplitude = amplitude;

        bool filled = _config.Filled;
        if (ImGui.Checkbox("Filled Triangle", ref filled))
            _config.Filled = filled;

        if (!_config.Filled)
        {
            float lineThickness = _config.LineThickness;
            if (ImGui.SliderFloat("Line Thickness", ref lineThickness, 1.0f, 10.0f))
                _config.LineThickness = lineThickness;
        }

        float sensitivity = _config.Sensitivity;
        if (ImGui.SliderFloat("Audio Sensitivity", ref sensitivity, 0.1f, 10.0f))
            _config.Sensitivity = sensitivity;

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Position");
        ImGui.Separator();

        int posX = _config.PositionX;
        if (ImGui.DragInt("Position X", ref posX, 1.0f, 0, _currentWindowSize.X))
        {
            _config.PositionX = posX;
        }

        int posY = _config.PositionY;
        if (ImGui.DragInt("Position Y", ref posY, 1.0f, 0, _currentWindowSize.Y))
        {
            _config.PositionY = posY;
        }

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Rotation");
        ImGui.Separator();

        int rotationSpeed = _config.RotationSpeed;
        if (ImGui.SliderInt("Rotation Speed (°/frame)", ref rotationSpeed, -90, 90))
            _config.RotationSpeed = rotationSpeed;

        int currentAngle = _config.CurrentAngle;
        if (ImGui.SliderInt("Manual Angle", ref currentAngle, 0, 360))
        {
            _config.CurrentAngle = currentAngle;
            _currentRotation = 0.0f; // Reset automatic rotation to avoid jumps
        }

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Audio Data Source");
        ImGui.Separator();

        bool useFFT = _config.UseFFT;
        if (ImGui.Checkbox("Use FFT Data", ref useFFT))
            _config.UseFFT = useFFT;

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Toggle between raw audio amplitude and FFT frequency spectrum.");
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
        if (ImGui.Checkbox("Time-based RGB (H:M:S)", ref useRealTimeColor))
        {
            _config.UseRealTimeColor = useRealTimeColor;
            if (useRealTimeColor) _config.UseTimeColor = false; // Disable other color mode
        }

        if (!_config.UseTimeColor && !_config.UseRealTimeColor)
        {
            var color = new System.Numerics.Vector3(_config.Color.X, _config.Color.Y, _config.Color.Z);
            if (ImGui.ColorEdit3("Triangle Color", ref color))
                _config.Color = new Vector3(color.X, color.Y, color.Z);
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

        ImGui.Spacing();
        if (ImGui.Button("Reset to Defaults"))
        {
            ResetToDefaults();
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
                Converters = { new VectorJsonConverter() }
            };
            return JsonSerializer.Serialize(_config, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save {Name} config: {ex.Message}");
            return "{}";
        }
    }

    public void LoadConfiguration(string json)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                Converters = { new VectorJsonConverter() }
            };
            var config = JsonSerializer.Deserialize<TriangleConfig>(json, options);
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

    public void ResetToDefaults()
    {
        _config = new TriangleConfig();
        // Set position to current window center
        _config.PositionX = _currentWindowSize.X / 2;
        _config.PositionY = _currentWindowSize.Y / 2;
        IsEnabled = _config.Enabled;
        _currentRotation = 0.0f;
    }

    public void Dispose()
    {
        if (_initialized)
        {
            GL.DeleteBuffer(_vertexBufferObject);
            GL.DeleteVertexArray(_vertexArrayObject);
            GL.DeleteProgram(_shaderProgram);
        }
        GC.SuppressFinalize(this);
    }
}
