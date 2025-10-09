
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Text.Json;
using ImGuiNET;
using CAudioVisualizer.Core;

namespace CAudioVisualizer.Visualizers;

public class SpectrumConfig
{
    public bool Enabled { get; set; } = true;
    public Vector3 Color { get; set; } = new Vector3(0.0f, 1.0f, 0.8f);
    public float Amplitude { get; set; } = 1.0f;
    public int BarCount { get; set; } = 64;
    public float BarWidth { get; set; } = 8.0f;
    public float BarSpacing { get; set; } = 2.0f;
    public float Radius { get; set; } = 200.0f;
    public float BarAngle { get; set; } = 0.0f; // 0 = linear, 360 = full circle
    public int PositionX { get; set; } = -1;
    public int PositionY { get; set; } = -1;
    public bool UseTimeColor { get; set; } = false;
    public bool UseRealTimeColor { get; set; } = false;
    public bool InvertColor { get; set; } = false;
    public bool EnableFadeTrail { get; set; } = false;
    public float FadeSpeed { get; set; } = 0.95f;
    public int TrailLength { get; set; } = 20;
    public bool UseFFT { get; set; } = true;
}

public struct SpectrumFrame
{
    public List<float> Vertices;
    public Vector3 Color;
    public float Alpha;
    public float BarWidth;
}

public class SpectrumVisualizer : IVisualizer, IConfigurable
{
    public bool IsEnabled
    {
        get => _config.Enabled;
        set => _config.Enabled = value;
    }

    private SpectrumConfig _config = new();
    private int _vertexBufferObject;
    private int _vertexArrayObject;
    private int _shaderProgram;
    private bool _initialized = false;
    private float[] _audioData = Array.Empty<float>();
    private float[] _fftData = Array.Empty<float>();
    private List<SpectrumFrame> _trailFrames = new();
    private VisualizerManager? _visualizerManager;

    private int _projectionLocation = -1;
    private readonly List<float> _tempVertexBuffer = new();

    private Vector2i CurrentWindowSize => _visualizerManager?.GetCurrentWindowSize() ?? new Vector2i(800, 600);

    public void SetVisualizerManager(VisualizerManager manager)
    {
        _visualizerManager = manager;
    }

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
            #version 330 core
            layout(location = 0) in vec3 aPosition;
            layout(location = 1) in vec4 aColor;

            uniform mat4 projection;
            out vec4 vertexColor;

            void main()
            {
                gl_Position = projection * vec4(aPosition, 1.0);
                vertexColor = aColor;
            }";

        string fragmentShaderSource = @"
            #version 330 core
            in vec4 vertexColor;
            out vec4 FragColor;

            void main()
            {
                FragColor = vertexColor;
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

        _projectionLocation = GL.GetUniformLocation(_shaderProgram, "projection");

        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
    }

    private void SetupVertexData()
    {
        _vertexArrayObject = GL.GenVertexArray();
        _vertexBufferObject = GL.GenBuffer();

        GL.BindVertexArray(_vertexArrayObject);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);

        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 7 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 7 * sizeof(float), 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
    }

    public void Update(float[] audioData, float[] fftData, double deltaTime)
    {
        _audioData = audioData;
        _fftData = fftData;
    }

    public void Render(Matrix4 projection)
    {
        if (!IsEnabled || !_initialized) return;

        var windowSize = CurrentWindowSize;

        if (_config.PositionX == -1)
            _config.PositionX = windowSize.X / 2;
        if (_config.PositionY == -1)
            _config.PositionY = windowSize.Y / 2;

        GL.UseProgram(_shaderProgram);
        GL.UniformMatrix4(_projectionLocation, false, ref projection);

        if (_config.EnableFadeTrail)
        {
            UpdateTrailFrames();
            RenderTrailFrames();
        }
        else
        {
            var currentFrame = GenerateCurrentSpectrum();
            if (currentFrame.Vertices.Count == 0) return;
            RenderSpectrum(currentFrame.Vertices, currentFrame.BarWidth);
        }
    }

    private void UpdateTrailFrames()
    {
        var currentFrame = GenerateCurrentSpectrum();
        _trailFrames.Insert(0, currentFrame);
        for (int i = _trailFrames.Count - 1; i >= 0; i--)
        {
            var frame = _trailFrames[i];
            frame.Alpha *= _config.FadeSpeed;
            if (frame.Alpha < 0.01f || i >= _config.TrailLength)
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
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.BindVertexArray(_vertexArrayObject);
        for (int i = _trailFrames.Count - 1; i >= 0; i--)
        {
            var frame = _trailFrames[i];
            if (frame.Vertices.Count == 0) continue;
            var fadedVertices = ApplyAlphaToVertices(frame.Vertices, frame.Alpha);
            RenderSpectrum(fadedVertices, frame.BarWidth);
        }
        GL.Disable(EnableCap.Blend);
    }

    private SpectrumFrame GenerateCurrentSpectrum()
    {
        float[] dataSource = _config.UseFFT ? _fftData : _audioData;
        if (dataSource.Length == 0)
        {
            return new SpectrumFrame
            {
                Vertices = new List<float>(),
                Color = _config.Color,
                Alpha = 1.0f,
                BarWidth = _config.BarWidth
            };
        }

        int count = Math.Min(_config.BarCount, dataSource.Length);
        float centerX = _config.PositionX;
        float centerY = _config.PositionY;

        Vector3 color = _config.UseTimeColor ? TimeColorHelper.GetTimeBasedColor() :
                       _config.UseRealTimeColor ? TimeColorHelper.GetRealTimeBasedColor() : _config.Color;
        if (_config.InvertColor) color = TimeColorHelper.InvertColor(color);

        var vertexBuffer = new List<float>(count * 14); // 2 vertices per bar

        var distributedRadius = _config.Radius / (count * 2);
        var adjustedBarWidth = _config.BarWidth + _config.BarWidth * distributedRadius;
        var adjustedBarSpacing = _config.BarSpacing + _config.BarSpacing * distributedRadius;

        // Morph between horizontal line and full circle, always top-aligned
        float w = count * (adjustedBarWidth + adjustedBarSpacing);
        float arcAngle = _config.BarAngle / 180 * MathF.PI;
        float r = w / arcAngle;
        Vector2 center = new Vector2(centerX, centerY + r);
        float thetaStart = MathF.PI / 2 + arcAngle / 2;
        float thetaEnd = MathF.PI / 2 - arcAngle / 2;

        for (int i = 0; i < count; i++)
        {
            int fftIndex = (int)(i * (dataSource.Length / (float)count));
            float value = dataSource[fftIndex] * _config.Amplitude;
            float t = count == 1 ? 0f : (float)i / (count - 1);
            float theta = thetaStart + (thetaEnd - thetaStart) * t;

            Vector2 start = arcAngle < 1e-3f
                ? new Vector2(centerX - w / 2 + t * w, centerY)
                : new Vector2(center.X + r * MathF.Cos(theta), center.Y - r * MathF.Sin(theta));
            // Bar direction: always perpendicular to curve (normal)
            Vector2 dir = Vector2.Normalize(new Vector2(MathF.Cos(theta), -MathF.Sin(theta)));
            Vector2 end = start + dir * value;
            // Start vertex
            vertexBuffer.Add(start.X);
            vertexBuffer.Add(start.Y);
            vertexBuffer.Add(0.0f);
            vertexBuffer.Add(color.X);
            vertexBuffer.Add(color.Y);
            vertexBuffer.Add(color.Z);
            vertexBuffer.Add(1.0f);
            // End vertex
            vertexBuffer.Add(end.X);
            vertexBuffer.Add(end.Y);
            vertexBuffer.Add(0.0f);
            vertexBuffer.Add(color.X);
            vertexBuffer.Add(color.Y);
            vertexBuffer.Add(color.Z);
            vertexBuffer.Add(1.0f);
        }

        return new SpectrumFrame
        {
            Vertices = vertexBuffer,
            Color = color,
            Alpha = 1.0f,
            BarWidth = adjustedBarWidth
        };
    }

    private List<float> ApplyAlphaToVertices(List<float> vertices, float alpha)
    {
        _tempVertexBuffer.Clear();
        if (_tempVertexBuffer.Capacity < vertices.Count)
            _tempVertexBuffer.Capacity = vertices.Count;
        _tempVertexBuffer.AddRange(vertices);
        for (int i = 6; i < _tempVertexBuffer.Count; i += 7)
        {
            _tempVertexBuffer[i] = alpha;
        }
        return _tempVertexBuffer;
    }

    private void RenderSpectrum(List<float> vertices, float barWidth)
    {
        if (vertices.Count == 0) return;
        GL.BindVertexArray(_vertexArrayObject);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices);
        GL.BufferData(BufferTarget.ArrayBuffer, span.Length * sizeof(float), ref span[0], BufferUsageHint.DynamicDraw);
        GL.LineWidth(barWidth);
        GL.Enable(EnableCap.LineSmooth);
        GL.Hint(HintTarget.LineSmoothHint, HintMode.Nicest);
        GL.DrawArrays(PrimitiveType.Lines, 0, vertices.Count / 7);
        GL.LineWidth(1.0f);
        GL.Disable(EnableCap.LineSmooth);
    }

    public void RenderConfigGui()
    {
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Spectrum Settings");
        ImGui.Separator();

        float amplitude = _config.Amplitude;
        if (ImGui.SliderFloat("Amplitude", ref amplitude, 0.1f, 2000.0f))
            _config.Amplitude = amplitude;

        float barWidth = _config.BarWidth;
        if (ImGui.SliderFloat("Bar Width", ref barWidth, 1.0f, 20.0f))
            _config.BarWidth = barWidth;

        float barSpacing = _config.BarSpacing;
        if (ImGui.SliderFloat("Bar Spacing", ref barSpacing, 0.0f, 20.0f))
            _config.BarSpacing = barSpacing;

        int barCount = _config.BarCount;
        if (ImGui.SliderInt("Bar Count", ref barCount, 4, 512))
            _config.BarCount = barCount;

        float radius = _config.Radius;
        if (ImGui.SliderFloat("Radius", ref radius, -500.0f, 500.0f))
            _config.Radius = radius;

        float barAngle = _config.BarAngle;
        if (ImGui.SliderFloat("Bar Angle (deg)", ref barAngle, 0.0f, 360.0f))
            _config.BarAngle = barAngle;

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Position");
        ImGui.Separator();

        int posX = _config.PositionX;
        if (ImGui.DragInt("Position X", ref posX, 1.0f, 0, CurrentWindowSize.X))
            _config.PositionX = Math.Max(0, Math.Min(CurrentWindowSize.X, posX));

        int posY = _config.PositionY;
        if (ImGui.DragInt("Position Y", ref posY, 1.0f, 0, CurrentWindowSize.Y))
            _config.PositionY = Math.Max(0, Math.Min(CurrentWindowSize.Y, posY));

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Audio Data Source");
        ImGui.Separator();

        bool useFFT = _config.UseFFT;
        if (ImGui.Checkbox("Use FFT Data", ref useFFT))
            _config.UseFFT = useFFT;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Toggle between raw audio amplitude and FFT frequency spectrum.");

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Colors");
        ImGui.Separator();

        bool useTimeColor = _config.UseTimeColor;
        if (ImGui.Checkbox("Rainbow Colors", ref useTimeColor))
        {
            _config.UseTimeColor = useTimeColor;
            if (useTimeColor) _config.UseRealTimeColor = false;
        }
        ImGui.SameLine();
        bool useRealTimeColor = _config.UseRealTimeColor;
        if (ImGui.Checkbox("Time-based RGB (H:M:S)", ref useRealTimeColor))
        {
            _config.UseRealTimeColor = useRealTimeColor;
            if (useRealTimeColor) _config.UseTimeColor = false;
        }
        ImGui.SameLine();
        bool invertColor = _config.InvertColor;
        if (ImGui.Checkbox("Invert Color", ref invertColor))
            _config.InvertColor = invertColor;

        if (!_config.UseTimeColor && !_config.UseRealTimeColor)
        {
            var color = new System.Numerics.Vector3(_config.Color.X, _config.Color.Y, _config.Color.Z);
            if (ImGui.ColorEdit3("Bar Color", ref color))
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
        ImGui.Separator();
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
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new VectorJsonConverter() }
            };
            return JsonSerializer.Serialize(_config, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save Spectrum config: {ex.Message}");
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
            var config = JsonSerializer.Deserialize<SpectrumConfig>(json, options);
            if (config != null)
            {
                _config = config;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load Spectrum config: {ex.Message}");
        }
    }

    public void ResetToDefaults()
    {
        _config = new SpectrumConfig();
        _config.PositionX = CurrentWindowSize.X / 2;
        _config.PositionY = CurrentWindowSize.Y / 2;
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
