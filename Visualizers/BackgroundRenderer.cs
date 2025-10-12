using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Text.Json;
using ImGuiNET;
using CAudioVisualizer.Core;

namespace CAudioVisualizer.Visualizers;

public enum BackgroundMode
{
    Static,
    AudioReactive
}

public enum BackgroundType
{
    Solid,
    Gradient
}

public enum GradientType
{
    Horizontal,
    Vertical,
    Circular
}

public class BackgroundConfig
{
    public BackgroundMode Mode { get; set; } = BackgroundMode.Static;
    public BackgroundType StaticType { get; set; } = BackgroundType.Solid;
    public GradientType GradientType { get; set; } = GradientType.Vertical;
    public Vector3 Color1 { get; set; } = new Vector3(0.0f, 0.0f, 0.0f);
    public Vector3 Color2 { get; set; } = new Vector3(0.2f, 0.0f, 0.4f);
    public bool UseTimeColor1 { get; set; } = false;
    public bool UseRealTimeColor1 { get; set; } = false;
    public bool UseTimeColor2 { get; set; } = false;
    public bool UseRealTimeColor2 { get; set; } = false;
    public bool InvertColor1 { get; set; } = false;
    public bool InvertColor2 { get; set; } = false;

    // Audio reactive settings
    public float TransitionTime { get; set; } = 2.0f;
    public float MaxLevelDecay { get; set; } = 0.95f;
    public bool UseRMS { get; set; } = true;
    public bool InvertGradient { get; set; } = false;
}

public class BackgroundRenderer : IVisualizer, IConfigurable
{
    public bool IsEnabled { get; set; } = true;

    private BackgroundConfig _config = new();
    private int _shaderProgram;
    private int _vao, _vbo, _ebo;
    private int _resolutionLocation, _modeLocation;
    private int _color1Location, _color2Location;
    private int _staticTypeLocation, _gradientTypeLocation, _audioIntensityLocation, _invertGradientLocation;
    private bool _initialized = false;
    private VisualizerManager? _visualizerManager;

    // Audio reactive state
    private float _currentAudioLevel = 0.0f;
    private float _maxAudioLevelEverHeard = 0.0f;
    private float _targetColorMix = 0.0f;
    private float _currentColorMix = 0.0f;

    // Cached render state to avoid redundant GL calls
    private Vector2i _lastWindowSize = new(-1, -1);
    private BackgroundMode _lastMode = (BackgroundMode)(-1);
    private BackgroundType _lastStaticType = (BackgroundType)(-1);
    private GradientType _lastGradientType = (GradientType)(-1);
    private Vector3 _lastColor1 = new(-1);
    private Vector3 _lastColor2 = new(-1);

    private Vector2i CurrentWindowSize => _visualizerManager?.GetCurrentWindowSize() ?? new Vector2i(800, 600);

    public void SetVisualizerManager(VisualizerManager manager)
    {
        _visualizerManager = manager;
    }

    public BackgroundRenderer()
    {
    }

    public void Initialize()
    {
        if (_initialized) return;

        // Enable smooth shading and improved quality
        GL.Enable(EnableCap.Dither);
        GL.Hint(HintTarget.GenerateMipmapHint, HintMode.Nicest);
        GL.Hint(HintTarget.FragmentShaderDerivativeHint, HintMode.Nicest);

        SetupShaders();
        SetupQuad();
        _initialized = true;
    }

    private void SetupShaders()
    {
        string vertexShaderSource = @"
            #version 330 core
            layout(location = 0) in vec2 aPosition;

            void main()
            {
                gl_Position = vec4(aPosition, 0.0, 1.0);
            }";

        string fragmentShaderSource = @"
            #version 330 core
            precision highp float;

            uniform vec2 u_resolution;
            uniform vec3 u_color1;
            uniform vec3 u_color2;
            uniform int u_mode;
            uniform int u_staticType;
            uniform int u_gradientType;
            uniform float u_colorMix;
            uniform bool u_invertGradient;

            out vec4 FragColor;

            // High-quality blue noise dithering with multiple octaves
            float blueNoise(vec2 coord) {
                vec2 p = coord * 0.01; // Scale down for smoother noise
                float noise = 0.0;
                noise += fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453) * 0.5;
                noise += fract(sin(dot(p * 2.0, vec2(93.9898, 67.345))) * 28374.2847) * 0.25;
                noise += fract(sin(dot(p * 4.0, vec2(41.2364, 93.2847))) * 67284.4738) * 0.125;
                return noise;
            }

            void main()
            {
                vec2 uv = gl_FragCoord.xy / u_resolution;
                vec3 color = u_color1;

                if (u_mode == 0) // Static
                {
                    if (u_staticType == 0) // Solid
                    {
                        color = u_color1;
                    }
                    else if (u_staticType == 1) // Gradient
                    {
                        float gradient = 0.0;

                        if (u_gradientType == 0) // Horizontal Gradient
                        {
                            gradient = u_invertGradient ? (1.0 - uv.x) : uv.x;
                        }
                        else if (u_gradientType == 1) // Vertical Gradient
                        {
                            gradient = u_invertGradient ? (1.0 - uv.y) : uv.y;
                        }
                        else if (u_gradientType == 2) // Circular Gradient
                        {
                            vec2 center = vec2(0.5, 0.5);
                            float dist = distance(uv, center);
                            float maxDist = 0.70710678; // sqrt(0.5) - distance to corner
                            float normalizedDist = clamp(dist / maxDist, 0.0, 1.0);
                            gradient = u_invertGradient ? (1.0 - normalizedDist) : normalizedDist;
                        }

                        // Apply much stronger dithering to break up banding
                        float noise = blueNoise(gl_FragCoord.xy);
                        float dither = (noise - 0.5) * 0.08;

                        // Add temporal dithering based on screen position
                        float spatialDither = fract(dot(gl_FragCoord.xy, vec2(0.1547, 0.2847))) - 0.5;
                        dither += spatialDither * (2.0 / 255.0);

                        // Use smooth RGB interpolation with dithering
                        float finalGradient = clamp(gradient + dither, 0.0, 1.0);
                        float smoothed = smoothstep(0.0, 1.0, finalGradient);
                        color = mix(u_color1, u_color2, smoothed);
                    }
                }
                else if (u_mode == 1) // AudioReactive
                {
                    if (u_staticType == 0) // Solid
                    {
                        // Simple smooth color mixing from Color1 (0) to Color2 (1)
                        color = mix(u_color1, u_color2, u_colorMix);
                    }
                    else if (u_staticType == 1) // Gradient
                    {
                        float gradient = 0.0;

                        if (u_gradientType == 0) // Horizontal Gradient
                        {
                            gradient = u_invertGradient ? (1.0 - uv.x) : uv.x;
                        }
                        else if (u_gradientType == 1) // Vertical Gradient
                        {
                            gradient = u_invertGradient ? (1.0 - uv.y) : uv.y;
                        }
                        else if (u_gradientType == 2) // Circular Gradient
                        {
                            vec2 center = vec2(0.5, 0.5);
                            float dist = distance(uv, center);
                            float maxDist = 0.70710678; // sqrt(0.5) - distance to corner
                            float normalizedDist = clamp(dist / maxDist, 0.0, 1.0);
                            gradient = u_invertGradient ? (1.0 - normalizedDist) : normalizedDist;
                        }

                        // Shift the gradient comparison point based on colorMix
                        float threshold = 1.0 - u_colorMix;

                        // Apply light dithering for smoothness
                        float noise = blueNoise(gl_FragCoord.xy);
                        float dither = (noise - 0.5) * 0.08;
                        float spatialDither = fract(dot(gl_FragCoord.xy, vec2(0.1547, 0.2847))) - 0.5;
                        dither += spatialDither * (2.0 / 255.0);

                        float finalGradient = clamp(gradient + dither, 0.0, 1.0);

                        // Use smooth step to create moving gradient boundary
                        float smoothed = smoothstep(threshold - 0.1, threshold + 0.5, finalGradient);
                        color = mix(u_color1, u_color2, smoothed);
                    }
                }

                FragColor = vec4(color, 1.0);
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

        // Get uniform locations
        _resolutionLocation = GL.GetUniformLocation(_shaderProgram, "u_resolution");
        _color1Location = GL.GetUniformLocation(_shaderProgram, "u_color1");
        _color2Location = GL.GetUniformLocation(_shaderProgram, "u_color2");
        _modeLocation = GL.GetUniformLocation(_shaderProgram, "u_mode");
        _staticTypeLocation = GL.GetUniformLocation(_shaderProgram, "u_staticType");
        _gradientTypeLocation = GL.GetUniformLocation(_shaderProgram, "u_gradientType");
        _audioIntensityLocation = GL.GetUniformLocation(_shaderProgram, "u_colorMix");
        _invertGradientLocation = GL.GetUniformLocation(_shaderProgram, "u_invertGradient");

        // Clean up shader objects
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
    }

    private void SetupQuad()
    {
        // Create a fullscreen quad
        float[] vertices = {
            -1.0f, -1.0f,  // Bottom left
             1.0f, -1.0f,  // Bottom right
             1.0f,  1.0f,  // Top right
            -1.0f,  1.0f   // Top left
        };

        uint[] indices = {
            0, 1, 2,
            2, 3, 0
        };

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        _ebo = GL.GenBuffer();

        GL.BindVertexArray(_vao);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        GL.BindVertexArray(0);
    }

    public void Update(float[] waveformData, float[] fftData, double deltaTime)
    {
        if (_config.Mode != BackgroundMode.AudioReactive)
        {
            _currentAudioLevel = 0.0f;
            _currentColorMix = 0.0f;
            return;
        }

        float audioLevel = 0.0f;

        if (_config.UseRMS)
        {
            // Use RMS (Root Mean Square) for smoother audio detection
            float sum = 0.0f;
            for (int i = 0; i < fftData.Length; i++)
            {
                sum += fftData[i] * fftData[i];
            }
            // arbitrary threshold to avoid weird audio clamps when no sound is present
            audioLevel = sum > 0.03f ? (float)Math.Sqrt(sum / fftData.Length) : 0.0f;
        }
        else
        {
            // Use peak detection as fallback
            for (int i = 0; i < waveformData.Length; i++)
            {
                audioLevel = Math.Max(audioLevel, Math.Abs(waveformData[i]));
            }
        }

        _currentAudioLevel = audioLevel;

        // Update max audio level with decay
        float decayPerSecond = (float)Math.Pow(_config.MaxLevelDecay, deltaTime);
        _maxAudioLevelEverHeard *= decayPerSecond;
        _maxAudioLevelEverHeard = Math.Max(_maxAudioLevelEverHeard, 0.001f); // Keep minimum threshold

        if (audioLevel > _maxAudioLevelEverHeard)
        {
            _maxAudioLevelEverHeard = audioLevel;
        }

        // Calculate target color mix (0 = Color1, 1 = Color2)
        _targetColorMix = _currentAudioLevel / _maxAudioLevelEverHeard;

        // Smooth transition towards target (takes _config.TransitionTime seconds)
        float transitionRate = (float)(1.0 / _config.TransitionTime * deltaTime);
        _currentColorMix += (_targetColorMix - _currentColorMix) * transitionRate;
        _currentColorMix = Math.Max(0.0f, Math.Min(1.0f, _currentColorMix));
    }

    public void Render(Matrix4 projection)
    {
        if (!_initialized || !IsEnabled) return;

        var windowSize = CurrentWindowSize;

        // Clear screen (always needed)
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // Use shader program
        GL.UseProgram(_shaderProgram);

        // Only update uniforms if values have changed
        if (_lastWindowSize != windowSize)
        {
            GL.Uniform2(_resolutionLocation, (float)windowSize.X, (float)windowSize.Y);
            _lastWindowSize = windowSize;
        }

        // Calculate colors (time-based colors change every frame)
        Vector3 color1 = _config.UseTimeColor1 ? TimeColorHelper.GetTimeBasedColor() :
                         _config.UseRealTimeColor1 ? TimeColorHelper.GetRealTimeBasedColor() : _config.Color1;
        Vector3 color2 = _config.UseTimeColor2 ? TimeColorHelper.GetTimeBasedColor() :
                         _config.UseRealTimeColor2 ? TimeColorHelper.GetRealTimeBasedColor() : _config.Color2;
        if (_config.InvertColor1) color1 = TimeColorHelper.InvertColor(color1);
        if (_config.InvertColor2) color2 = TimeColorHelper.InvertColor(color2);

        if (_lastColor1 != color1 || _config.UseTimeColor1 || _config.UseRealTimeColor1)
        {
            GL.Uniform3(_color1Location, color1);
            _lastColor1 = color1;
        }

        if (_lastColor2 != color2 || _config.UseTimeColor2 || _config.UseRealTimeColor2)
        {
            GL.Uniform3(_color2Location, color2);
            _lastColor2 = color2;
        }

        if (_lastMode != _config.Mode)
        {
            GL.Uniform1(_modeLocation, (int)_config.Mode);
            _lastMode = _config.Mode;
        }

        if (_lastStaticType != _config.StaticType)
        {
            GL.Uniform1(_staticTypeLocation, (int)_config.StaticType);
            _lastStaticType = _config.StaticType;
        }

        if (_lastGradientType != _config.GradientType)
        {
            GL.Uniform1(_gradientTypeLocation, (int)_config.GradientType);
            _lastGradientType = _config.GradientType;
        }

        // Always update color mix for AudioReactive mode
        GL.Uniform1(_audioIntensityLocation, _currentColorMix);

        // Always update gradient inversion setting
        GL.Uniform1(_invertGradientLocation, _config.InvertGradient ? 1 : 0);

        // Render fullscreen quad
        GL.BindVertexArray(_vao);
        GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);

        GL.UseProgram(0);
    }

    public void RenderConfigGui()
    {
        ImGui.Text("Background Settings");
        ImGui.Separator();

        var modes = Enum.GetNames<BackgroundMode>();
        int currentMode = (int)_config.Mode;
        if (ImGui.Combo("Background Mode", ref currentMode, modes, modes.Length))
        {
            _config.Mode = (BackgroundMode)currentMode;
        }

        if (ImGui.Button("Swap Colors"))
        {
            var temp = _config.Color1;
            _config.Color1 = _config.Color2;
            _config.Color2 = temp;

            // Swap checkbox states
            var tempUseTimeColor = _config.UseTimeColor1;
            _config.UseTimeColor1 = _config.UseTimeColor2;
            _config.UseTimeColor2 = tempUseTimeColor;

            var tempUseRealTimeColor = _config.UseRealTimeColor1;
            _config.UseRealTimeColor1 = _config.UseRealTimeColor2;
            _config.UseRealTimeColor2 = tempUseRealTimeColor;

            var tempInvertColor = _config.InvertColor1;
            _config.InvertColor1 = _config.InvertColor2;
            _config.InvertColor2 = tempInvertColor;
        }

        // Primary Color
        bool useTimeColor1 = _config.UseTimeColor1;
        if (ImGui.Checkbox("Rainbow Primary Color", ref useTimeColor1))
        {
            _config.UseTimeColor1 = useTimeColor1;
            if (useTimeColor1) _config.UseRealTimeColor1 = false; // Disable other color mode
        }
        ImGui.SameLine();
        ImGui.SetCursorPosX(200);
        bool useRealTimeColor1 = _config.UseRealTimeColor1;
        if (ImGui.Checkbox("Time-based RGB Primary (H:M:S)", ref useRealTimeColor1))
        {
            _config.UseRealTimeColor1 = useRealTimeColor1;
            if (useRealTimeColor1) _config.UseTimeColor1 = false; // Disable other color mode
        }
        ImGui.SameLine();
        ImGui.SetCursorPosX(455);
        bool invertColor1 = _config.InvertColor1;
        if (ImGui.Checkbox("Invert Primary Color", ref invertColor1))
        {
            _config.InvertColor1 = invertColor1;
        }

        if (!_config.UseTimeColor1 && !_config.UseRealTimeColor1)
        {
            var color1 = new System.Numerics.Vector3(_config.Color1.X, _config.Color1.Y, _config.Color1.Z);
            if (ImGui.ColorEdit3("Primary Color", ref color1))
            {
                _config.Color1 = new Vector3(color1.X, color1.Y, color1.Z);
            }
        }

        // Secondary Color
        bool useTimeColor2 = _config.UseTimeColor2;
        if (ImGui.Checkbox("Rainbow Secondary Color", ref useTimeColor2))
        {
            _config.UseTimeColor2 = useTimeColor2;
            if (useTimeColor2) _config.UseRealTimeColor2 = false; // Disable other color mode
        }
        ImGui.SameLine();
        ImGui.SetCursorPosX(200);
        bool useRealTimeColor2 = _config.UseRealTimeColor2;
        if (ImGui.Checkbox("Time-based RGB Secondary (H:M:S)", ref useRealTimeColor2))
        {
            _config.UseRealTimeColor2 = useRealTimeColor2;
            if (useRealTimeColor2) _config.UseTimeColor2 = false; // Disable other color mode
        }
        ImGui.SameLine();
        ImGui.SetCursorPosX(455);
        bool invertColor2 = _config.InvertColor2;
        if (ImGui.Checkbox("Invert Secondary Color", ref invertColor2))
        {
            _config.InvertColor2 = invertColor2;
        }

        if (!_config.UseTimeColor2 && !_config.UseRealTimeColor2)
        {
            var color2 = new System.Numerics.Vector3(_config.Color2.X, _config.Color2.Y, _config.Color2.Z);
            if (ImGui.ColorEdit3("Secondary Color", ref color2))
            {
                _config.Color2 = new Vector3(color2.X, color2.Y, color2.Z);
            }
        }

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Rendering Type");
        ImGui.Separator();

        var staticTypes = Enum.GetNames<BackgroundType>();
        int currentStaticType = (int)_config.StaticType;
        if (ImGui.Combo("Rendering Type", ref currentStaticType, staticTypes, staticTypes.Length))
        {
            _config.StaticType = (BackgroundType)currentStaticType;
        }

        if (_config.StaticType == BackgroundType.Gradient)
        {
            var gradientTypes = Enum.GetNames<GradientType>();
            int currentGradientType = (int)_config.GradientType;
            if (ImGui.Combo("Gradient Type", ref currentGradientType, gradientTypes, gradientTypes.Length))
            {
                _config.GradientType = (GradientType)currentGradientType;
            }

            bool invertGradient = _config.InvertGradient;
            if (ImGui.Checkbox("Invert Gradient Direction", ref invertGradient))
            {
                _config.InvertGradient = invertGradient;
            }
        }

        if (_config.Mode == BackgroundMode.AudioReactive)
        {
            ImGui.Spacing();
            ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Audio Reactive Settings");
            ImGui.Separator();

            if (_config.StaticType == BackgroundType.Solid)
            {
                ImGui.TextColored(new System.Numerics.Vector4(0.8f, 0.8f, 0.5f, 1.0f), "Solid mode: Colors blend based on audio level");
            }
            else
            {
                ImGui.TextColored(new System.Numerics.Vector4(0.8f, 0.8f, 0.5f, 1.0f), "Gradient mode: Gradient shifts based on audio level");
            }

            float transitionTime = _config.TransitionTime;
            if (ImGui.SliderFloat("Transition Time (s)", ref transitionTime, 0.1f, 10.0f))
            {
                _config.TransitionTime = transitionTime;
            }

            float maxLevelDecay = _config.MaxLevelDecay;
            if (ImGui.SliderFloat("Max Level Decay", ref maxLevelDecay, 0.1f, 0.999f, "%.3f"))
            {
                _config.MaxLevelDecay = maxLevelDecay;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("How fast the max volume level decays (higher = slower decay)");
            }

            bool useRMS = _config.UseRMS;
            if (ImGui.Checkbox("Use RMS Audio Detection", ref useRMS))
            {
                _config.UseRMS = useRMS;
                _currentAudioLevel = 0.0f;
                _maxAudioLevelEverHeard = 0.0f;
                _currentColorMix = 0.0f;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Use RMS for smoother audio detection vs peak detection");
            }

            ImGui.Spacing();
            ImGui.Text($"Current Volume: {_currentAudioLevel:F3}");
            ImGui.Text($"Max Volume:     {_maxAudioLevelEverHeard:F3}");
            ImGui.SameLine();
            if (ImGui.Button("Reset Max Volume"))
            {
                _maxAudioLevelEverHeard = 0.0f;
            }
            ImGui.Text($"Color Mix:      {_currentColorMix:F3}");
            ImGui.SameLine();
            if (ImGui.Button("Reset Color Mix"))
            {
                _currentColorMix = 0.0f;
            }
        }
        else // Static mode
        {
            ImGui.Spacing();
            if (_config.StaticType == BackgroundType.Solid)
            {
                ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Static solid color display");
            }
            else
            {
                ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Static gradient display");
            }
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
            Console.WriteLine($"Failed to save Background config: {ex.Message}");
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
            var config = JsonSerializer.Deserialize<BackgroundConfig>(json, options);
            if (config != null)
            {
                _config = config;
                // Reset cached state to force uniform updates
                _lastMode = (BackgroundMode)(-1);
                _lastStaticType = (BackgroundType)(-1);
                _lastGradientType = (GradientType)(-1);
                _lastColor1 = new(-1);
                _lastColor2 = new(-1);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load Background config: {ex.Message}");
        }
    }

    public void ResetToDefaults()
    {
        _config = new BackgroundConfig();
        // Reset cached state to force uniform updates
        _lastMode = (BackgroundMode)(-1);
        _lastGradientType = (GradientType)(-1);
        _lastColor1 = new(-1);
        _lastColor2 = new(-1);
        _currentAudioLevel = 0.0f;
        _maxAudioLevelEverHeard = 0.0f;
        _currentColorMix = 0.0f;
        _targetColorMix = 0.0f;
    }

    public void Dispose()
    {
        if (!_initialized) return;

        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        GL.DeleteBuffer(_ebo);
        GL.DeleteProgram(_shaderProgram);
    }
}
