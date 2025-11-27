using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Text.Json;
using ImGuiNET;
using CAudioVisualizer.Core;

namespace CAudioVisualizer.Visualizers;

public class CustomShaderConfig
{
    public bool Enabled { get; set; } = true;
    public Vector3 Color { get; set; } = new Vector3(1.0f, 0.5f, 0.0f);
    public float Intensity { get; set; } = 10.0f;
    public float Scale { get; set; } = 0.75f;
    public float Speed { get; set; } = 1.0f;
    public float AudioReactivity { get; set; } = 0.4f;
    public int PositionX { get; set; } = -1;
    public int PositionY { get; set; } = -1;
    public bool UseTimeColor { get; set; } = false;
    public bool UseRealTimeColor { get; set; } = false;
    public bool InvertColor { get; set; } = false;
    public bool UseFFT { get; set; } = true;
    public string FragmentShaderSource { get; set; } = "";
}

public class CustomShaderVisualizer : IVisualizer, IConfigurable
{
    public bool IsEnabled
    {
        get => _config.Enabled;
        set => _config.Enabled = value;
    }

    private CustomShaderConfig _config = new();
    private int _vertexBufferObject;
    private int _vertexArrayObject;
    private int _shaderProgram;
    private bool _initialized = false;
    private float[] _audioData = Array.Empty<float>();
    private float[] _fftData = Array.Empty<float>();
    private VisualizerManager? _visualizerManager;
    private float _time = 0f;
    private float _smoothedAudioAmplitude = 0f;
    private string _shaderCompilationError = "";
    private bool _hasShaderError = false;

    private int _projectionLocation = -1;
    private int _timeLocation = -1;
    private int _resolutionLocation = -1;
    private int _audioAmplitudeLocation = -1;
    private int _intensityLocation = -1;
    private int _scaleLocation = -1;
    private int _colorLocation = -1;
    private int _positionLocation = -1;

    private float[] _vertices = new float[18]; // 6 vertices * 3 components
    private Vector2i _lastWindowSize = new Vector2i(-1, -1);

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

    private const string DefaultFragmentShaderSource = @"#version 330 core
in vec2 fragCoord;
in vec2 resolution;
out vec4 FragColor;

uniform float uTime;
uniform float uAudioAmplitude;
uniform float uIntensity;
uniform float uScale;
uniform vec3 uColor;
uniform vec2 uPosition;

vec2 cos_vec2(vec2 v) {
    return vec2(cos(v.x), cos(v.y));
}

vec4 sin_vec4(vec4 v) {
    return vec4(sin(v.x), sin(v.y), sin(v.z), sin(v.w));
}

vec4 exp_vec4(vec4 v) {
    return vec4(exp(v.x), exp(v.y), exp(v.z), exp(v.w));
}

vec4 tanh_vec4(vec4 v) {
    return vec4(tanh(v.x), tanh(v.y), tanh(v.z), tanh(v.w));
}

void main()
{
    vec4 o = vec4(0.0);
    vec2 r = resolution;
    vec2 FC = fragCoord - (uPosition - r / 2.0);
    float t = uTime;

    // Shader from
    // https://x.com/XorDev/status/1894123951401378051
    // vec2 p=(FC.xy*2.-r)/r.y,l,v=p*(1.-(l+=abs(.7-dot(p,p))))/.2;for(float i;i++<8.;o+=(sin(v.xyyx)+1.)*abs(v.x-v.y)*.2)v+=cos(v.yx*i+vec2(0,i)+t)/i+.7;o=tanh(exp(p.y*vec4(1,-1,-2,0))*exp(-4.*l.x)/o);
    vec2 p = (FC * 2.0 - r) / (r.y * uScale);
    vec2 l = vec2(0.0);
    vec2 i = vec2(0.0, 0.0);

    l += 4.0 - 4.0 * abs(0.7 - dot(p, p));
    vec2 v = p * l;

    for(; i.y++ < 8.0; ) {
        vec2 vyx = vec2(v.y, v.x);
        vec4 vxyyx = vec4(v.x, v.y, v.y, v.x);

        o += (sin_vec4(vxyyx) + 1.0) * abs(v.x - v.y);
        v += cos_vec2(vyx * i.y + i + t) / i.y + 0.7;
    }

    vec4 expTerm = exp_vec4(l.x - 4.0 - p.y * vec4(-1.0, 1.0, 2.0, 0.0));

    // Apply audio reactivity to intensity with smoothing
    float dynamicIntensity = uIntensity * (1.0 + uAudioAmplitude * 0.5);
    o = tanh_vec4(dynamicIntensity * expTerm / o);

    // This makes NaN areas blend in naturally instead of being black
    float validSum = 0.0;
    float validCount = 0.0;
    if (!isnan(o.r)) { validSum += o.r; validCount += 1.0; }
    if (!isnan(o.g)) { validSum += o.g; validCount += 1.0; }
    if (!isnan(o.b)) { validSum += o.b; validCount += 1.0; }
    float avgValid = (validCount > 0.0) ? (validSum / validCount) : 1.0;

    if (isnan(o.r)) o.r = avgValid;
    if (isnan(o.g)) o.g = avgValid;
    if (isnan(o.b)) o.b = avgValid;

    o.rgb *= uColor;

    float alpha = (o.r + o.g + o.b) / 3.0;
    FragColor = vec4(o.rgb, alpha);
}";

    private void SetupShaders()
    {
        if (string.IsNullOrEmpty(_config.FragmentShaderSource))
        {
            _config.FragmentShaderSource = DefaultFragmentShaderSource;
        }

        var windowSize = CurrentWindowSize;

        if (_config.PositionX == -1)
            _config.PositionX = windowSize.X / 2;
        if (_config.PositionY == -1)
            _config.PositionY = windowSize.Y / 2;

        string vertexShaderSource = @"
            #version 330 core
            layout(location = 0) in vec3 aPosition;

            uniform mat4 projection;
            out vec2 fragCoord;
            out vec2 resolution;

            uniform vec2 uResolution;

            void main()
            {
                gl_Position = projection * vec4(aPosition, 1.0);
                // Convert from screen space to shader coordinate space
                fragCoord = aPosition.xy;
                resolution = uResolution;
            }";

        int vertexShader = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vertexShader, vertexShaderSource);
        GL.CompileShader(vertexShader);

        int fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fragmentShader, _config.FragmentShaderSource);
        GL.CompileShader(fragmentShader);

        GL.GetShader(fragmentShader, ShaderParameter.CompileStatus, out int fragmentStatus);
        if (fragmentStatus == 0)
        {
            string error = GL.GetShaderInfoLog(fragmentShader);
            _shaderCompilationError = $"Fragment Shader Error: {error}";
            _hasShaderError = true;
            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);
            return;
        }

        if (_shaderProgram != 0)
        {
            GL.DeleteProgram(_shaderProgram);
        }

        _shaderProgram = GL.CreateProgram();
        GL.AttachShader(_shaderProgram, vertexShader);
        GL.AttachShader(_shaderProgram, fragmentShader);
        GL.LinkProgram(_shaderProgram);

        // Check program linking
        GL.GetProgram(_shaderProgram, GetProgramParameterName.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
        {
            string error = GL.GetProgramInfoLog(_shaderProgram);
            _shaderCompilationError = $"Shader Linking Error: {error}";
            _hasShaderError = true;
            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);
            GL.DeleteProgram(_shaderProgram);
            _shaderProgram = 0;
            return;
        }

        _hasShaderError = false;
        _shaderCompilationError = "";

        _projectionLocation = GL.GetUniformLocation(_shaderProgram, "projection");
        _timeLocation = GL.GetUniformLocation(_shaderProgram, "uTime");
        _resolutionLocation = GL.GetUniformLocation(_shaderProgram, "uResolution");
        _audioAmplitudeLocation = GL.GetUniformLocation(_shaderProgram, "uAudioAmplitude");
        _intensityLocation = GL.GetUniformLocation(_shaderProgram, "uIntensity");
        _scaleLocation = GL.GetUniformLocation(_shaderProgram, "uScale");
        _colorLocation = GL.GetUniformLocation(_shaderProgram, "uColor");
        _positionLocation = GL.GetUniformLocation(_shaderProgram, "uPosition");

        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
    }

    private void SetupVertexData()
    {
        _vertexArrayObject = GL.GenVertexArray();
        _vertexBufferObject = GL.GenBuffer();

        GL.BindVertexArray(_vertexArrayObject);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);

        var windowSize = CurrentWindowSize;
        _vertices[0] = 0f; _vertices[1] = 0f; _vertices[2] = 0f;
        _vertices[3] = windowSize.X; _vertices[4] = 0f; _vertices[5] = 0f;
        _vertices[6] = windowSize.X; _vertices[7] = windowSize.Y; _vertices[8] = 0f;
        _vertices[9] = 0f; _vertices[10] = 0f; _vertices[11] = 0f;
        _vertices[12] = windowSize.X; _vertices[13] = windowSize.Y; _vertices[14] = 0f;
        _vertices[15] = 0f; _vertices[16] = windowSize.Y; _vertices[17] = 0f;

        GL.BufferData(BufferTarget.ArrayBuffer, _vertices.Length * sizeof(float), _vertices, BufferUsageHint.DynamicDraw);
        _lastWindowSize = windowSize;

        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
    }

    public void Update(float[] audioData, float[] fftData, double deltaTime)
    {
        _audioData = audioData;
        _fftData = fftData;

        float[] dataSource = _config.UseFFT ? _fftData : _audioData;
        float currentAmplitude = 0f;

        if (dataSource.Length > 0)
        {
            float sum = 0f;
            int sampleCount = Math.Min(64, dataSource.Length);
            for (int i = 0; i < sampleCount; i++)
            {
                sum += Math.Abs(dataSource[i]);
            }
            currentAmplitude = sum / sampleCount * _config.AudioReactivity;
        }

        // Smooth the audio amplitude with heavy smoothing for fluid time modulation
        float smoothingFactor = 1.0f - (float)Math.Exp(-deltaTime * 8.0);
        _smoothedAudioAmplitude = _smoothedAudioAmplitude * (1.0f - smoothingFactor) + currentAmplitude * smoothingFactor;

        // Accumulate time with audio-based speed boost - always moves forward
        float audioSpeedBoost = 1.0f + Math.Max(0.0f, _smoothedAudioAmplitude * 2.0f);
        _time += (float)deltaTime * _config.Speed * audioSpeedBoost;
    }

    public void Render(Matrix4 projection)
    {
        if (!IsEnabled || !_initialized || _hasShaderError || _shaderProgram == 0) return;


        Vector3 color = _config.UseTimeColor ? TimeColorHelper.GetTimeBasedColor() :
                       _config.UseRealTimeColor ? TimeColorHelper.GetRealTimeBasedColor() : _config.Color;
        if (_config.InvertColor) color = TimeColorHelper.InvertColor(color);

        var windowSize = CurrentWindowSize;
        // Only update if window size changed
        if (windowSize != _lastWindowSize)
        {
            _vertices[0] = 0f; _vertices[1] = 0f; _vertices[2] = 0f;
            _vertices[3] = windowSize.X; _vertices[4] = 0f; _vertices[5] = 0f;
            _vertices[6] = windowSize.X; _vertices[7] = windowSize.Y; _vertices[8] = 0f;
            _vertices[9] = 0f; _vertices[10] = 0f; _vertices[11] = 0f;
            _vertices[12] = windowSize.X; _vertices[13] = windowSize.Y; _vertices[14] = 0f;
            _vertices[15] = 0f; _vertices[16] = windowSize.Y; _vertices[17] = 0f;

            GL.BindVertexArray(_vertexArrayObject);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
            GL.BufferData(BufferTarget.ArrayBuffer, _vertices.Length * sizeof(float), _vertices, BufferUsageHint.DynamicDraw);

            _lastWindowSize = windowSize;
        }

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        GL.UseProgram(_shaderProgram);
        GL.BindVertexArray(_vertexArrayObject);

        GL.UniformMatrix4(_projectionLocation, false, ref projection);
        GL.Uniform1(_timeLocation, _time);
        GL.Uniform2(_resolutionLocation, (float)windowSize.X, (float)windowSize.Y);
        GL.Uniform1(_audioAmplitudeLocation, _smoothedAudioAmplitude);
        GL.Uniform1(_intensityLocation, _config.Intensity);
        GL.Uniform1(_scaleLocation, _config.Scale);
        GL.Uniform3(_colorLocation, color.X, color.Y, color.Z);
        GL.Uniform2(_positionLocation, (float)_config.PositionX, (float)_config.PositionY);

        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    public void RenderConfigGui()
    {
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Shader Visualizer");
        ImGui.Separator();

        float intensity = _config.Intensity;
        if (ImGui.SliderFloat("Intensity", ref intensity, 0.1f, 20.0f))
            _config.Intensity = intensity;

        float scale = _config.Scale;
        if (ImGui.SliderFloat("Scale", ref scale, 0.1f, 2.0f))
            _config.Scale = scale;

        float speed = _config.Speed;
        if (ImGui.SliderFloat("Speed", ref speed, 0.1f, 4.0f))
            _config.Speed = speed;

        float audioReactivity = _config.AudioReactivity;
        float maxReactivity = _config.UseFFT ? 1.5f : 50.0f;
        if (ImGui.SliderFloat("Audio Reactivity", ref audioReactivity, 0.0f, maxReactivity))
            _config.AudioReactivity = audioReactivity;

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
        {
            // Map the current value to the new range proportionally
            float oldMax = _config.UseFFT ? 1.5f : 50.0f;
            float newMax = useFFT ? 1.5f : 50.0f;
            _config.AudioReactivity = _config.AudioReactivity / oldMax * newMax;
            _config.UseFFT = useFFT;
        }

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
        {
            _config.InvertColor = invertColor;
        }

        if (!_config.UseTimeColor && !_config.UseRealTimeColor)
        {
            var color = new System.Numerics.Vector3(_config.Color.X, _config.Color.Y, _config.Color.Z);
            if (ImGui.ColorEdit3("Base Color", ref color))
                _config.Color = new Vector3(color.X, color.Y, color.Z);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Shader editor
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Fragment Shader");
        ImGui.Separator();

        if (_hasShaderError)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1.0f, 0.3f, 0.3f, 1.0f), "Compilation Error:");
            ImGui.TextWrapped(_shaderCompilationError);
        }
        else
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.3f, 1.0f, 0.3f, 1.0f), "Shader compiled successfully");
        }

        ImGui.Spacing();

        // Calculate available space for the text editor
        float availableHeight = ImGui.GetContentRegionAvail().Y - 60; // Reserve space for buttons below
        float editorHeight = Math.Max(300, availableHeight); // Minimum 300, but expand if more space available

        string shaderSource = _config.FragmentShaderSource;
        if (ImGui.InputTextMultiline("##FragmentShader", ref shaderSource, 10000, new System.Numerics.Vector2(-1, editorHeight)))
        {
            if (shaderSource != _config.FragmentShaderSource)
            {
                _config.FragmentShaderSource = shaderSource;
                shaderSource = _config.FragmentShaderSource;
                SetupShaders();
            }
        }

        if (ImGui.Button("Reset Shader to Default"))
        {
            _config.FragmentShaderSource = DefaultFragmentShaderSource;
            SetupShaders();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Reset to Defaults"))
        {
            ResetToDefaults();
            SetupShaders();
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
            Console.WriteLine($"Failed to save Shader config: {ex.Message}");
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
            var config = JsonSerializer.Deserialize<CustomShaderConfig>(json, options);
            if (config != null)
            {
                _config = config;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load Shader config: {ex.Message}");
        }
    }

    public void ResetToDefaults()
    {
        _config = new CustomShaderConfig();
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
