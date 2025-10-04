using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Text.Json;
using ImGuiNET;
using CAudioVisualizer.Core;

namespace CAudioVisualizer.Visualizers;

public enum BackgroundMode
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
    public BackgroundMode Mode { get; set; } = BackgroundMode.Solid;
    public GradientType GradientType { get; set; } = GradientType.Vertical;
    public Vector3 Color1 { get; set; } = new Vector3(0.0f, 0.0f, 0.0f); // Primary color
    public Vector3 Color2 { get; set; } = new Vector3(0.2f, 0.0f, 0.4f); // Secondary color for gradients
}

public class BackgroundRenderer : IVisualizer, IConfigurable
{
    public bool IsEnabled { get; set; } = true;

    private BackgroundConfig _config = new();
    private int _shaderProgram;
    private int _vao, _vbo, _ebo;
    private int _resolutionLocation, _modeLocation;
    private int _color1Location, _color2Location;
    private int _gradientTypeLocation;
    private bool _initialized = false;
    private VisualizerManager? _visualizerManager;

    // Cached render state to avoid redundant GL calls
    private Vector2i _lastWindowSize = new(-1, -1);
    private BackgroundMode _lastMode = (BackgroundMode)(-1);
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
            uniform int u_gradientType;

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

                if (u_mode == 0) // Solid
                {
                    color = u_color1;
                }
                else if (u_mode == 1) // Gradient
                {
                    float gradient = 0.0;

                    if (u_gradientType == 0) // Horizontal Gradient
                    {
                        gradient = uv.x;
                    }
                    else if (u_gradientType == 1) // Vertical Gradient
                    {
                        gradient = uv.y;
                    }
                    else if (u_gradientType == 2) // Circular Gradient
                    {
                        vec2 center = vec2(0.5, 0.5);
                        float dist = distance(uv, center);
                        float maxDist = 0.70710678; // sqrt(0.5) - distance to corner
                        gradient = clamp(dist / maxDist, 0.0, 1.0);
                    }

                    // Apply much stronger dithering to break up banding
                    float noise = blueNoise(gl_FragCoord.xy);
                    float dither = (noise - 0.5) * 0.08; // Much stronger dithering

                    // Add temporal dithering based on screen position
                    float spatialDither = fract(dot(gl_FragCoord.xy, vec2(0.1547, 0.2847))) - 0.5;
                    dither += spatialDither * (2.0 / 255.0);

                    // Use smooth RGB interpolation with dithering
                    float finalGradient = clamp(gradient + dither, 0.0, 1.0);
                    float smoothed = smoothstep(0.0, 1.0, finalGradient);
                    color = mix(u_color1, u_color2, smoothed);
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
        _gradientTypeLocation = GL.GetUniformLocation(_shaderProgram, "u_gradientType");

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
        // Background doesn't use audio data
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

        if (_lastColor1 != _config.Color1)
        {
            GL.Uniform3(_color1Location, _config.Color1);
            _lastColor1 = _config.Color1;
        }

        if (_lastColor2 != _config.Color2)
        {
            GL.Uniform3(_color2Location, _config.Color2);
            _lastColor2 = _config.Color2;
        }

        if (_lastMode != _config.Mode)
        {
            GL.Uniform1(_modeLocation, (int)_config.Mode);
            _lastMode = _config.Mode;
        }

        if (_lastGradientType != _config.GradientType)
        {
            GL.Uniform1(_gradientTypeLocation, (int)_config.GradientType);
            _lastGradientType = _config.GradientType;
        }

        // Render fullscreen quad
        GL.BindVertexArray(_vao);
        GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);

        GL.UseProgram(0);
    }

    public void Dispose()
    {
        if (_initialized)
        {
            GL.DeleteVertexArray(_vao);
            GL.DeleteBuffer(_vbo);
            GL.DeleteBuffer(_ebo);
            GL.DeleteProgram(_shaderProgram);
        }
    }

    public void RenderConfigGui()
    {
        ImGui.Text("Background Settings");
        ImGui.Separator();

        // Background mode selection
        var modes = Enum.GetNames<BackgroundMode>();
        int currentMode = (int)_config.Mode;
        if (ImGui.Combo("Background Mode", ref currentMode, modes, modes.Length))
        {
            _config.Mode = (BackgroundMode)currentMode;
        }

        // Primary color picker
        var color1 = new System.Numerics.Vector3(_config.Color1.X, _config.Color1.Y, _config.Color1.Z);
        if (ImGui.ColorEdit3("Primary Color", ref color1))
        {
            _config.Color1 = new Vector3(color1.X, color1.Y, color1.Z);
        }

        // Secondary color (for gradients)
        if (_config.Mode == BackgroundMode.Gradient)
        {
            var color2 = new System.Numerics.Vector3(_config.Color2.X, _config.Color2.Y, _config.Color2.Z);
            if (ImGui.ColorEdit3("Secondary Color", ref color2))
            {
                _config.Color2 = new Vector3(color2.X, color2.Y, color2.Z);
            }

            // Swap colors button
            ImGui.SameLine();
            if (ImGui.Button("Swap Colors"))
            {
                var temp = _config.Color1;
                _config.Color1 = _config.Color2;
                _config.Color2 = temp;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Swap primary and secondary colors");
            }

            var gradientTypes = Enum.GetNames<GradientType>();
            int currentGradientType = (int)_config.GradientType;
            if (ImGui.Combo("Gradient Type", ref currentGradientType, gradientTypes, gradientTypes.Length))
            {
                _config.GradientType = (GradientType)currentGradientType;
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Reset to Default"))
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
    }
}
