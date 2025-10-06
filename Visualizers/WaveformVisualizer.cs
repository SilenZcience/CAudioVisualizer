using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Text.Json;
using ImGuiNET;
using CAudioVisualizer.Core;

namespace CAudioVisualizer.Visualizers;

public struct WaveformFrame
{
    public List<float> Vertices;
    public Vector3 Color;
    public float Alpha;
    public float LineWidth;
}

public class WaveformConfig
{
    public bool Enabled { get; set; } = true;
    public Vector3 Color { get; set; } = new Vector3(0.0f, 1.0f, 0.0f);
    public float Amplitude { get; set; } = 1.0f;
    public int PositionY { get; set; } = -1;
    public float LineThickness { get; set; } = 2.0f;
    public int StartX { get; set; } = 0;
    public int EndX { get; set; } = -1;
    public bool UseTimeColor { get; set; } = false;
    public bool UseRealTimeColor { get; set; } = false;
    public bool InvertColor { get; set; } = false;
    public float PositionX { get; set; } = 0.5f;
    public bool EnableFadeTrail { get; set; } = false;
    public float FadeSpeed { get; set; } = 0.95f;
    public int TrailLength { get; set; } = 20;
    public bool UseFFT { get; set; } = false;
    public bool FlipV { get; set; } = false;
    public bool FlipH { get; set; } = false;
}

public class WaveformVisualizer : IVisualizer, IConfigurable
{
    public bool IsEnabled
    {
        get => _config.Enabled;
        set => _config.Enabled = value;
    }

    private WaveformConfig _config = new();
    private int _vertexBufferObject;
    private int _vertexArrayObject;
    private int _shaderProgram;
    private bool _initialized = false;
    private float[] _audioData = Array.Empty<float>();
    private float[] _fftData = Array.Empty<float>();
    private List<WaveformFrame> _trailFrames = new();
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

        // Create and compile shaders
        int vertexShader = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vertexShader, vertexShaderSource);
        GL.CompileShader(vertexShader);

        int fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fragmentShader, fragmentShaderSource);
        GL.CompileShader(fragmentShader);

        // Create shader program
        _shaderProgram = GL.CreateProgram();
        GL.AttachShader(_shaderProgram, vertexShader);
        GL.AttachShader(_shaderProgram, fragmentShader);
        GL.LinkProgram(_shaderProgram);

        _projectionLocation = GL.GetUniformLocation(_shaderProgram, "projection");

        // Clean up shader objects
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
    }

    private void SetupVertexData()
    {
        // Generate buffers
        _vertexArrayObject = GL.GenVertexArray();
        _vertexBufferObject = GL.GenBuffer();

        GL.BindVertexArray(_vertexArrayObject);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);

        // Position attribute
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 7 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        // Color attribute (RGBA)
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

        // Initialize positions if not set yet
        if (_config.PositionY == -1)
            _config.PositionY = windowSize.Y / 2;
        if (_config.EndX == -1)
            _config.EndX = windowSize.X;

        GL.UseProgram(_shaderProgram);

        GL.UniformMatrix4(_projectionLocation, false, ref projection);

        if (_config.EnableFadeTrail)
        {
            UpdateTrailFrames(windowSize);
            RenderTrailFrames();
        }
        else
        {
            var currentWaveform = GenerateCurrentWaveform(windowSize);
            if (currentWaveform.Vertices.Count == 0) return;

            // Upload vertex data and render
            RenderWaveform(currentWaveform.Vertices, currentWaveform.LineWidth);
        }
    }

    private void UpdateTrailFrames(Vector2i windowSize)
    {
        var currentWaveform = GenerateCurrentWaveform(windowSize);

        // Add current waveform to trail
        _trailFrames.Insert(0, currentWaveform);

        // Update alpha of existing frames and remove old ones
        for (int i = _trailFrames.Count - 1; i >= 0; i--)
        {
            var frame = _trailFrames[i];
            frame.Alpha *= _config.FadeSpeed;

            // Remove frames that are too transparent or exceed trail length
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

        // Render trail frames from oldest to newest (so newest appears on top)
        for (int i = _trailFrames.Count - 1; i >= 0; i--)
        {
            var frame = _trailFrames[i];
            if (frame.Vertices.Count == 0) continue;

            // Apply alpha to vertices
            var fadedVertices = ApplyAlphaToVertices(frame.Vertices, frame.Alpha);

            RenderWaveform(fadedVertices, frame.LineWidth);
        }

        GL.Disable(EnableCap.Blend);
    }

    private WaveformFrame GenerateCurrentWaveform(Vector2i windowSize)
    {
        float[] dataSource = _config.UseFFT ? _fftData : _audioData;

        if (dataSource.Length == 0)
        {
            return new WaveformFrame
            {
                Vertices = new List<float>(),
                Color = _config.Color,
                Alpha = 1.0f,
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
            return new WaveformFrame
            {
                Vertices = new List<float>(),
                Color = _config.Color,
                Alpha = 1.0f,
                LineWidth = _config.LineThickness
            };
        }

        int waveformWidth = (int)(endPixel - startPixel);

        if (waveformWidth < 2)
        {
            return new WaveformFrame
            {
                Vertices = new List<float>(),
                Color = _config.Color,
                Alpha = 1.0f,
                LineWidth = _config.LineThickness
            };
        }

        var vertexBuffer = new List<float>(waveformWidth * 7);

        Vector3 color = _config.UseTimeColor ? TimeColorHelper.GetTimeBasedColor() :
                       _config.UseRealTimeColor ? TimeColorHelper.GetRealTimeBasedColor() : _config.Color;
        if (_config.InvertColor) color = TimeColorHelper.InvertColor(color);

        // Generate waveform points
        for (int x = 0; x < waveformWidth; x++)
        {
            // Handle flipV option
            int actualX = _config.FlipV ? (waveformWidth - 1 - x) : x;

            // Calculate sample index with interpolation for smoother curves
            float exactIndex = actualX * (dataSource.Length / (float)waveformWidth);
            int sampleIndex = (int)exactIndex;

            // Scale sample and calculate Y position
            float scaledSample = dataSource[sampleIndex] * _config.Amplitude;

            // Apply horizontal flip if enabled (invert amplitude)
            if (_config.FlipH)
                scaledSample = -scaledSample;

            float y = centerY - scaledSample * (windowSize.Y * 0.4f); // Use 40% of height for waveform range

            // Calculate X position - direct pixel mapping like GDI+
            float pixelX = startPixel + x;

            vertexBuffer.Add(pixelX);           // X
            vertexBuffer.Add(y);                // Y
            vertexBuffer.Add(0.0f);             // Z
            vertexBuffer.Add(color.X);          // R
            vertexBuffer.Add(color.Y);          // G
            vertexBuffer.Add(color.Z);          // B
            vertexBuffer.Add(1.0f);             // A
        }

        return new WaveformFrame
        {
            Vertices = vertexBuffer,
            Color = color,
            Alpha = 1.0f,
            LineWidth = _config.LineThickness
        };
    }

    private List<float> ApplyAlphaToVertices(List<float> vertices, float alpha)
    {
        _tempVertexBuffer.Clear();
        if (_tempVertexBuffer.Capacity < vertices.Count)
            _tempVertexBuffer.Capacity = vertices.Count;

        _tempVertexBuffer.AddRange(vertices);

        // Apply alpha to alpha component only (every 7th float is alpha)
        for (int i = 6; i < _tempVertexBuffer.Count; i += 7)
        {
            _tempVertexBuffer[i] = alpha;
        }

        return _tempVertexBuffer;
    }

    private void RenderWaveform(List<float> vertices, float lineWidth)
    {
        if (vertices.Count == 0) return;

        // Upload vertex data
        GL.BindVertexArray(_vertexArrayObject);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices);
        GL.BufferData(BufferTarget.ArrayBuffer, span.Length * sizeof(float), ref span[0], BufferUsageHint.DynamicDraw);

        GL.LineWidth(lineWidth);
        GL.Enable(EnableCap.LineSmooth);
        GL.Hint(HintTarget.LineSmoothHint, HintMode.Nicest);

        // Draw waveform as line strip
        GL.DrawArrays(PrimitiveType.LineStrip, 0, vertices.Count / 7); // 7 floats per vertex (3 pos + 4 color)

        GL.LineWidth(1.0f);
        GL.Disable(EnableCap.LineSmooth);
    }

    public void RenderConfigGui()
    {
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Waveform Settings");
        ImGui.Separator();

        float amplitude = _config.Amplitude;
        if (ImGui.SliderFloat("Amplitude", ref amplitude, 0.01f, 5.0f))
            _config.Amplitude = amplitude;

        float lineThickness = _config.LineThickness;
        if (ImGui.SliderFloat("Line Thickness", ref lineThickness, 1.0f, 10.0f))
            _config.LineThickness = lineThickness;

        bool flipV = _config.FlipV;
        if (ImGui.Checkbox("Flip Waveform Vertically", ref flipV))
            _config.FlipV = flipV;

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Flips the waveform vertically (left <-> right).");
        }

        bool flipH = _config.FlipH;
        if (ImGui.Checkbox("Flip Waveform Horizontally", ref flipH))
            _config.FlipH = flipH;

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Flips the waveform horizontally (up <-> down).");
        }

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Position");
        ImGui.Separator();

        int posY = _config.PositionY;
        if (ImGui.DragInt("Position Y", ref posY, 1.0f, 0, CurrentWindowSize.Y))
        {
            _config.PositionY = Math.Max(0, Math.Min(CurrentWindowSize.Y, posY));
        }

        int startX = _config.StartX;
        if (ImGui.DragInt("Start X", ref startX, 1.0f, 0, CurrentWindowSize.X))
        {
            _config.StartX = Math.Max(0, Math.Min(CurrentWindowSize.X, startX));
            // Ensure StartX doesn't exceed EndX
            if (_config.StartX >= _config.EndX)
                _config.EndX = Math.Min(CurrentWindowSize.X, _config.StartX + 10);
        }

        int endX = _config.EndX;
        if (ImGui.DragInt("End X", ref endX, 1.0f, 0, CurrentWindowSize.X))
        {
            _config.EndX = Math.Max(0, Math.Min(CurrentWindowSize.X, endX));
            // Ensure EndX doesn't go below StartX
            if (_config.EndX <= _config.StartX)
                _config.StartX = Math.Max(0, _config.EndX - 10);
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
        ImGui.SameLine();
        bool useRealTimeColor = _config.UseRealTimeColor;
        if (ImGui.Checkbox("Time-based RGB (H:M:S)", ref useRealTimeColor))
        {
            _config.UseRealTimeColor = useRealTimeColor;
            if (useRealTimeColor) _config.UseTimeColor = false; // Disable other color mode
        }
        ImGui.SameLine();
        bool invertColor = _config.InvertColor;
        if (ImGui.Checkbox("Invert Color", ref invertColor))
        {
            _config.InvertColor = invertColor;
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
            Console.WriteLine($"Failed to save Waveform config: {ex.Message}");
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
            var config = JsonSerializer.Deserialize<WaveformConfig>(json, options);
            if (config != null)
            {
                _config = config;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load Waveform config: {ex.Message}");
        }
    }

    public void ResetToDefaults()
    {
        _config = new WaveformConfig();
        // Set positions to current window defaults
        _config.PositionY = CurrentWindowSize.Y / 2;
        _config.EndX = CurrentWindowSize.X;
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
