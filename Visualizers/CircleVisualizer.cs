using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Text.Json;
using ImGuiNET;
using CAudioVisualizer.Core;

namespace CAudioVisualizer.Visualizers;

public struct CircleDot
{
    public Vector3 Position;
    public Vector3 Color;
    public float Size;
}

public struct CircleFrame
{
    public List<Vector3> DotPositions;
    public Vector3 Color;
    public float Brightness;
    public float DotSize;
}

public class CircleConfig
{
    public bool Enabled { get; set; } = true;
    public Vector3 Color { get; set; } = new Vector3(0.0f, 1.0f, 1.0f); // Cyan
    public float CircleSize { get; set; } = 75.0f; // Base radius multiplier
    public int DotsMin { get; set; } = 300; // Minimum number of dots
    public int DotsMax { get; set; } = 500; // Maximum number of dots
    public float DotSize { get; set; } = 2.0f; // Size of individual dots
    public int PositionX { get; set; } = -1; // X position in pixels (will be set to center on first use)
    public int PositionY { get; set; } = -1; // Y position in pixels (will be set to center on first use)
    public float Sensitivity { get; set; } = 1.0f; // Amplitude sensitivity
    public bool UseTimeColor { get; set; } = false; // Use rainbow colors
    public bool UseRealTimeColor { get; set; } = false; // Use actual time as RGB
    public bool EnableFadeTrail { get; set; } = false; // Enable fade trail effect
    public float FadeSpeed { get; set; } = 0.95f; // How fast circles fade (0.9 = slow, 0.99 = fast)
    public int TrailLength { get; set; } = 20; // Maximum number of trail circles
    public bool UseFFT { get; set; } = false; // Use FFT data instead of waveform data
}

public class CircleVisualizer : IVisualizer, IConfigurable
{
    public string Name => "Circle";
    public string DisplayName => "Circle";
    public bool IsEnabled
    {
        get => _config.Enabled;
        set => _config.Enabled = value;
    }

    private CircleConfig _config = new();
    private int _vertexBufferObject;
    private int _vertexArrayObject;
    private int _shaderProgram;
    private bool _initialized = false;
    private Random _random;
    private float[] _audioData = Array.Empty<float>();
    private float[] _fftData = Array.Empty<float>();
    private List<CircleFrame> _trailFrames = new();
    private VisualizerManager? _visualizerManager;

    private int _projectionLocation = -1;
    private int _pointSizeLocation = -1;
    private readonly List<float> _vertexBuffer = new();
    private readonly List<float> _tempVertexBuffer = new();

    private Vector2i CurrentWindowSize => _visualizerManager?.GetCurrentWindowSize() ?? new Vector2i(800, 600);

    public void SetVisualizerManager(VisualizerManager manager)
    {
        _visualizerManager = manager;
    }

    public CircleVisualizer()
    {
        _random = new Random(12345);
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
            #version 460 core
            layout (location = 0) in vec3 aPosition;
            layout (location = 1) in vec3 aColor;

            uniform mat4 projection;
            uniform float pointSize;
            out vec3 vertexColor;

            void main()
            {
                gl_Position = projection * vec4(aPosition, 1.0);
                gl_PointSize = pointSize;
                vertexColor = aColor;
            }";

        string fragmentShaderSource = @"
            #version 460 core

            in vec3 vertexColor;
            out vec4 FragColor;

            void main()
            {
                // Create circular points
                vec2 coord = gl_PointCoord - vec2(0.5);
                if (length(coord) > 0.5)
                    discard;
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

        _projectionLocation = GL.GetUniformLocation(_shaderProgram, "projection");
        _pointSizeLocation = GL.GetUniformLocation(_shaderProgram, "pointSize");

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
    }

    public void Render(Matrix4 projection, Vector2i windowSize)
    {
        if (!IsEnabled || !_initialized) return;

        // Initialize position to center if not set yet
        if (_config.PositionX == -1)
            _config.PositionX = windowSize.X / 2;
        if (_config.PositionY == -1)
            _config.PositionY = windowSize.Y / 2;

        GL.UseProgram(_shaderProgram);
        GL.UniformMatrix4(_projectionLocation, false, ref projection);

        if (_config.EnableFadeTrail)
        {
            // Update trail frames - fade existing ones and add new one
            UpdateTrailFrames(windowSize);

            // Render all trail frames
            RenderTrailFrames();
        }
        else
        {
            // Normal rendering without trail
            GL.Uniform1(_pointSizeLocation, _config.DotSize);

            // Generate circle dots
            var vertices = GenerateCircleDots(windowSize);
            if (vertices.Count == 0) return;

            // Upload vertex data
            GL.BindVertexArray(_vertexArrayObject);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);

            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices);
            GL.BufferData(BufferTarget.ArrayBuffer, span.Length * sizeof(float), ref span[0], BufferUsageHint.DynamicDraw);

            // Enable point sprites
            GL.Enable(EnableCap.ProgramPointSize);

            // Draw points
            GL.DrawArrays(PrimitiveType.Points, 0, vertices.Count / 6);

            GL.Disable(EnableCap.ProgramPointSize);
        }
    }

    private List<float> GenerateCircleDots(Vector2i windowSize)
    {
        // Generate circle frame and convert to vertex list using reusable buffer
        var circleFrame = GenerateCurrentCircle(windowSize);
        _vertexBuffer.Clear();

        foreach (var dotPos in circleFrame.DotPositions)
        {
            // Add vertex: position (x, y, z) + color (r, g, b)
            _vertexBuffer.Add(dotPos.X);
            _vertexBuffer.Add(dotPos.Y);
            _vertexBuffer.Add(dotPos.Z);
            _vertexBuffer.Add(circleFrame.Color.X);
            _vertexBuffer.Add(circleFrame.Color.Y);
            _vertexBuffer.Add(circleFrame.Color.Z);
        }

        return _vertexBuffer;
    }

    private void UpdateTrailFrames(Vector2i windowSize)
    {
        // Generate current circle
        var currentCircle = GenerateCurrentCircle(windowSize);

        // Add current circle to trail
        _trailFrames.Insert(0, currentCircle);

        // Fade existing frames and remove completely faded ones
        for (int i = _trailFrames.Count - 1; i >= 0; i--)
        {
            var frame = _trailFrames[i];
            frame.Brightness *= _config.FadeSpeed;

            // Remove circles that are too dim or exceed trail length
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

    private CircleFrame GenerateCurrentCircle(Vector2i windowSize)
    {
        var dotPositions = new List<Vector3>();

        // Choose data source based on config
        float[] dataSource = _config.UseFFT ? _fftData : _audioData;

        // Check if we have audio data to work with
        if (dataSource.Length == 0)
            return new CircleFrame
            {
                DotPositions = dotPositions,
                Color = _config.Color,
                Brightness = 1.0f,
                DotSize = _config.DotSize
            };

        // Calculate center position - exactly like GDI+ version
        float centerX = _config.PositionX;
        float centerY = _config.PositionY;

        // Calculate number of dots (randomized between min and max) - exactly like GDI+ version
        int dots = _random.Next(_config.DotsMin, _config.DotsMax + 1);
        _random = new Random(12345);

        // Make sure dots is even for the two-loop pattern - exactly like GDI+ version
        if (dots % 2 != 0) dots++;

        // Get current color
        Vector3 color = _config.UseTimeColor ? TimeColorHelper.GetTimeBasedColor() :
                       _config.UseRealTimeColor ? TimeColorHelper.GetRealTimeBasedColor() : _config.Color;

        // First loop: Step by 2, use -PI (upper semicircle) - exactly like GDI+ version
        for (int i = 1; i <= dots; i += 2)
        {
            // Get random audio data point - exactly like GDI+ version with bounds checking
            if (dataSource.Length == 0) continue;
            int dataIndex = _random.Next(0, dataSource.Length);
            float fft = Math.Abs(dataSource[dataIndex]);

            // Apply sensitivity and power scaling similar to original (sqrt of sqrt) - exactly like GDI+ version
            float rootRootFft = _config.CircleSize * (float)Math.Sqrt(Math.Sqrt(fft * 100000 * _config.Sensitivity));

            float iterationByDots = (float)i / dots;
            float angle = (float)(-Math.PI * iterationByDots); // Negative PI for upper semicircle

            float x = centerX + (float)(Math.Cos(angle) * rootRootFft);
            float y = centerY + (float)(Math.Sin(angle) * rootRootFft);

            dotPositions.Add(new Vector3(x, y, 0.0f));
        }

        // Second loop: Step by 2, use +PI (lower semicircle) - exactly like GDI+ version
        for (int i = 2; i <= dots; i += 2)
        {
            // Get random audio data point - exactly like GDI+ version with bounds checking
            if (dataSource.Length == 0) continue;
            int dataIndex = _random.Next(0, dataSource.Length);
            float fft = Math.Abs(dataSource[dataIndex]);

            // Apply sensitivity and power scaling - exactly like GDI+ version
            float rootRootFft = _config.CircleSize * (float)Math.Sqrt(Math.Sqrt(fft * 100000 * _config.Sensitivity));

            float iterationByDots = (float)i / dots;
            float angle = (float)(Math.PI * iterationByDots); // Positive PI for lower semicircle

            float x = centerX + (float)(Math.Cos(angle) * rootRootFft);
            float y = centerY + (float)(Math.Sin(angle) * rootRootFft);

            dotPositions.Add(new Vector3(x, y, 0.0f));
        }

        return new CircleFrame
        {
            DotPositions = dotPositions,
            Color = color,
            Brightness = 1.0f,
            DotSize = _config.DotSize
        };
    }

    private void RenderTrailFrames()
    {
        GL.BindVertexArray(_vertexArrayObject);

        // Draw circles in reverse order (oldest first, newest last)
        for (int i = _trailFrames.Count - 1; i >= 0; i--)
        {
            var frame = _trailFrames[i];

            // Create vertex data with faded color using reusable buffer
            _tempVertexBuffer.Clear();
            Vector3 fadedColor = frame.Color * frame.Brightness;

            foreach (var dotPos in frame.DotPositions)
            {
                _tempVertexBuffer.Add(dotPos.X);
                _tempVertexBuffer.Add(dotPos.Y);
                _tempVertexBuffer.Add(dotPos.Z);
                _tempVertexBuffer.Add(fadedColor.X);
                _tempVertexBuffer.Add(fadedColor.Y);
                _tempVertexBuffer.Add(fadedColor.Z);
            }

            if (_tempVertexBuffer.Count == 0) continue;

            GL.Uniform1(_pointSizeLocation, frame.DotSize);

            // Upload and draw
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_tempVertexBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, span.Length * sizeof(float), ref span[0], BufferUsageHint.DynamicDraw);

            // Enable point sprites
            GL.Enable(EnableCap.ProgramPointSize);

            // Draw points
            GL.DrawArrays(PrimitiveType.Points, 0, _tempVertexBuffer.Count / 6);

            GL.Disable(EnableCap.ProgramPointSize);
        }
    }

    public void RenderConfigGui()
    {
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Circle Settings");
        ImGui.Separator();

        float circleSize = _config.CircleSize;
        if (ImGui.SliderFloat("Circle Size", ref circleSize, 5.0f, 150.0f))
            _config.CircleSize = circleSize;

        int dotsMin = _config.DotsMin;
        if (ImGui.SliderInt("Minimum Dots", ref dotsMin, 10, 1000))
        {
            _config.DotsMin = dotsMin;
            if (_config.DotsMax < _config.DotsMin)
                _config.DotsMax = _config.DotsMin; // Ensure max is not less than min
        }

        int dotsMax = _config.DotsMax;
        if (ImGui.SliderInt("Maximum Dots", ref dotsMax, _config.DotsMin, 1000))
            _config.DotsMax = dotsMax;

        float dotSize = _config.DotSize;
        if (ImGui.SliderFloat("Dot Size", ref dotSize, 1.0f, 20.0f))
            _config.DotSize = dotSize;

        float sensitivity = _config.Sensitivity;
        if (ImGui.SliderFloat("Audio Sensitivity", ref sensitivity, 0.1f, 10.0f))
            _config.Sensitivity = sensitivity;

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Position");
        ImGui.Separator();

        int posX = _config.PositionX;
        if (ImGui.DragInt("Position X", ref posX, 1.0f, 0, CurrentWindowSize.X))
        {
            _config.PositionX = posX;
        }

        int posY = _config.PositionY;
        if (ImGui.DragInt("Position Y", ref posY, 1.0f, 0, CurrentWindowSize.Y))
        {
            _config.PositionY = posY;
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
            if (ImGui.ColorEdit3("Circle Color", ref color))
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
            var config = JsonSerializer.Deserialize<CircleConfig>(json, options);
            if (config != null)
            {
                _config = config;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load {Name} config: {ex.Message}");
        }
    }

    public void ResetToDefaults()
    {
        _config = new CircleConfig();
        // Set position to current window center
        _config.PositionX = CurrentWindowSize.X / 2;
        _config.PositionY = CurrentWindowSize.Y / 2;
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
