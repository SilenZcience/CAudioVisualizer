using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Text.Json;
using ImGuiNET;
using AudioVisualizerC.Core;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace AudioVisualizerC.Visualizers;

public struct SpectrumBar
{
    public Vector3 Position;
    public Vector3 Color;
    public float Height;
    public float Width;
}

public struct SpectrumFrame
{
    public List<SpectrumBar> Bars;
    public Vector3 Color;
    public float Brightness;
}

public class SpectrumBarsConfig
{
    public bool Enabled { get; set; } = true;
    public Vector3 Color { get; set; } = new Vector3(1.0f, 0.5f, 0.0f); // Orange
    public int BarCount { get; set; } = 256; // Number of frequency bars (more bins for better resolution)
    public float MaxBarHeight { get; set; } = 400.0f; // Maximum bar height in pixels
    public float Sensitivity { get; set; } = 50000.0f; // Audio sensitivity multiplier (much higher for proper scaling)
    public int PositionX { get; set; } = -1; // X position in pixels (will be set to center on first use)
    public int PositionY { get; set; } = -1; // Y position in pixels (will be set to bottom on first use)
    public bool UseTimeColor { get; set; } = false; // Use rainbow colors
    public bool UseRealTimeColor { get; set; } = false; // Use actual time as RGB
    public bool EnableFadeTrail { get; set; } = false; // Enable fade trail effect
    public float FadeSpeed { get; set; } = 0.95f; // How fast bars fade (0.9 = slow, 0.99 = fast)
    public int TrailLength { get; set; } = 20; // Maximum number of trail frames
    public bool UseLogarithmicScale { get; set; } = true; // Use logarithmic frequency scaling
    public float MinFrequency { get; set; } = 20.0f; // Minimum frequency to display (Hz)
    public float MaxFrequency { get; set; } = 20000.0f; // Maximum frequency to display (Hz)
}

public class SpectrumBarsVisualizer : IVisualizer, IConfigurable
{
    public string Name => "Spectrum Bars";
    public string DisplayName => "Spectrum Bars";
    public bool IsEnabled { get; set; } = true;

    private SpectrumBarsConfig _config = new();
    private int _vertexBufferObject;
    private int _vertexArrayObject;
    private int _shaderProgram;
    private bool _initialized = false;
    private float[] _audioData = Array.Empty<float>();
    private Vector2i _currentWindowSize = new Vector2i(800, 600);
    private List<SpectrumFrame> _trailFrames = new();

    // FFT processing
    private Complex32[] _fftBuffer = new Complex32[2048];
    private float[] _spectrumData = new float[1024];
    private float[] _rawSpectrum = new float[1024]; // Raw unsmoothed spectrum data
    // Removed smoothing for authentic real-time response

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

    public void Update(float[] waveformData, double deltaTime)
    {
        _audioData = waveformData;
        ProcessFFT();
    }

    private void ProcessFFT()
    {
        if (_audioData.Length == 0) return;

        // Prepare FFT buffer
        int fftSize = Math.Min(_audioData.Length, _fftBuffer.Length);
        for (int i = 0; i < fftSize; i++)
        {
            _fftBuffer[i] = new Complex32(_audioData[i], 0);
        }

        // Perform FFT
        Fourier.Forward(_fftBuffer, FourierOptions.Matlab);

        // Calculate raw magnitudes without smoothing for true real-time values
        int spectrumSize = fftSize / 2;
        for (int i = 0; i < spectrumSize && i < _spectrumData.Length; i++)
        {
            float magnitude = _fftBuffer[i].Magnitude;
            _spectrumData[i] = magnitude;

            // Store raw magnitude directly - no smoothing for authentic real-time response
            _rawSpectrum[i] = magnitude;
        }
    }

    public void Render(Matrix4 projection, Vector2i windowSize)
    {
        if (!IsEnabled || !_initialized) return;

        // Update current window size for config GUI
        _currentWindowSize = windowSize;

        // Initialize positions and bar width if not set yet
        if (_config.PositionX == -1)
            _config.PositionX = 0; // Start at left edge of screen
        if (_config.PositionY == -1)
            _config.PositionY = windowSize.Y; // Bottom of screen (bars grow upward)

        // Always calculate bar width to fit full screen width perfectly
        float barWidth = (float)windowSize.X / _config.BarCount;

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
            // Generate current spectrum bars
            var currentFrame = GenerateCurrentSpectrum(windowSize);
            if (currentFrame.Bars.Count == 0) return;

            // Render current frame
            RenderSpectrumFrame(currentFrame);
        }
    }

    private void UpdateTrailFrames(Vector2i windowSize)
    {
        // Generate current spectrum
        var currentSpectrum = GenerateCurrentSpectrum(windowSize);

        // Add current spectrum to trail
        _trailFrames.Insert(0, currentSpectrum);

        // Fade existing frames and remove completely faded ones
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

    private SpectrumFrame GenerateCurrentSpectrum(Vector2i windowSize)
    {
        var bars = new List<SpectrumBar>();

        // Calculate bar width to fit full screen width perfectly
        float barWidth = (float)windowSize.X / _config.BarCount;

        if (_rawSpectrum.Length == 0)
        {
            return new SpectrumFrame
            {
                Bars = bars,
                Color = _config.Color,
                Brightness = 1.0f
            };
        }

        // Get current color
        Vector3 color = _config.UseTimeColor ? TimeColorHelper.GetTimeBasedColor() :
                       _config.UseRealTimeColor ? TimeColorHelper.GetRealTimeBasedColor() : _config.Color;

        // Calculate bar positioning to span full width (no spacing)
        float startX = _config.PositionX; // Start from left edge

        // Generate bars
        for (int i = 0; i < _config.BarCount; i++)
        {
            // Calculate frequency bin index with interpolation for smoother bars
            float exactBinIndex = GetExactFrequencyBinIndex(i, _config.BarCount, _rawSpectrum.Length);
            float magnitude = GetInterpolatedMagnitude(exactBinIndex);
            float scaledMagnitude = magnitude * _config.Sensitivity;

            // Calculate bar height with proper scaling (normalize to 0-1 range first)
            float normalizedMagnitude = Math.Min(scaledMagnitude / 100000.0f, 1.0f); // Normalize to 0-1
            float barHeight = normalizedMagnitude * _config.MaxBarHeight;            // Calculate bar position (no spacing between bars)
            float barX = startX + i * barWidth;
            float barY = _config.PositionY - barHeight; // Bars grow upward

            // Create bar
            var bar = new SpectrumBar
            {
                Position = new Vector3(barX, barY, 0.0f),
                Color = color,
                Height = barHeight,
                Width = barWidth
            };

            bars.Add(bar);
        }

        return new SpectrumFrame
        {
            Bars = bars,
            Color = color,
            Brightness = 1.0f
        };
    }

    private int GetFrequencyBinIndex(int barIndex, int totalBars, int spectrumLength)
    {
        if (_config.UseLogarithmicScale)
        {
            // Improved logarithmic frequency mapping with better distribution
            float logMin = (float)Math.Log10(_config.MinFrequency);
            float logMax = (float)Math.Log10(_config.MaxFrequency);
            float logRange = logMax - logMin;

            float normalizedIndex = (float)barIndex / (totalBars - 1);
            float logFreq = logMin + normalizedIndex * logRange;
            float frequency = (float)Math.Pow(10, logFreq);

            // Convert frequency to bin index (assuming 44.1kHz sample rate)
            float exactBinIndex = frequency * spectrumLength / 22050.0f;
            int binIndex = (int)exactBinIndex;

            // Ensure each bar gets a unique bin index to avoid identical heights
            binIndex = Math.Max(binIndex, barIndex); // Force minimum progression
            return Math.Min(binIndex, spectrumLength - 1);
        }
        else
        {
            // Linear frequency mapping with frequency range support - only use first half of FFT (no mirroring)
            int usableSpectrum = spectrumLength / 2; // Only use non-mirrored half

            // Map bar index to frequency linearly within the specified range
            float normalizedIndex = (float)barIndex / (totalBars - 1);
            float frequency = _config.MinFrequency + normalizedIndex * (_config.MaxFrequency - _config.MinFrequency);

            // Convert frequency to bin index (assuming 44.1kHz sample rate)
            int binIndex = (int)(frequency * usableSpectrum / 22050.0f);
            return Math.Min(binIndex, usableSpectrum - 1);
        }
    }

    private float GetExactFrequencyBinIndex(int barIndex, int totalBars, int spectrumLength)
    {
        if (_config.UseLogarithmicScale)
        {
            // Logarithmic frequency mapping returning exact float index
            float logMin = (float)Math.Log10(_config.MinFrequency);
            float logMax = (float)Math.Log10(_config.MaxFrequency);
            float logRange = logMax - logMin;

            float normalizedIndex = (float)barIndex / (totalBars - 1);
            float logFreq = logMin + normalizedIndex * logRange;
            float frequency = (float)Math.Pow(10, logFreq);

            // Convert frequency to exact bin index (assuming 44.1kHz sample rate)
            float exactBinIndex = frequency * spectrumLength / 22050.0f;
            return Math.Min(exactBinIndex, spectrumLength - 1);
        }
        else
        {
            // Linear frequency mapping with frequency range support - only use first half of FFT (no mirroring)
            int usableSpectrum = spectrumLength / 2; // Only use non-mirrored half

            // Map bar index to frequency linearly within the specified range
            float normalizedIndex = (float)barIndex / (totalBars - 1);
            float frequency = _config.MinFrequency + normalizedIndex * (_config.MaxFrequency - _config.MinFrequency);

            // Convert frequency to bin index (assuming 44.1kHz sample rate)
            float exactBinIndex = frequency * usableSpectrum / 22050.0f;
            return Math.Min(exactBinIndex, usableSpectrum - 1);
        }
    }

    private float GetInterpolatedMagnitude(float exactBinIndex)
    {
        if (exactBinIndex < 0 || _rawSpectrum.Length == 0) return 0.0f;

        // Get integer part and fractional part
        int lowerBin = (int)Math.Floor(exactBinIndex);
        int upperBin = lowerBin + 1;
        float fraction = exactBinIndex - lowerBin;

        // Clamp to valid range
        lowerBin = Math.Max(0, Math.Min(lowerBin, _rawSpectrum.Length - 1));
        upperBin = Math.Max(0, Math.Min(upperBin, _rawSpectrum.Length - 1));

        // Linear interpolation between adjacent raw spectrum bins
        float lowerMagnitude = _rawSpectrum[lowerBin];
        float upperMagnitude = _rawSpectrum[upperBin];

        return lowerMagnitude + fraction * (upperMagnitude - lowerMagnitude);
    }

    private void RenderTrailFrames()
    {
        // Render trail frames from oldest to newest (so newest appears on top)
        for (int i = _trailFrames.Count - 1; i >= 0; i--)
        {
            var frame = _trailFrames[i];
            if (frame.Bars.Count == 0) continue;

            // Apply brightness to frame
            var fadedFrame = new SpectrumFrame
            {
                Bars = frame.Bars.Select(bar => new SpectrumBar
                {
                    Position = bar.Position,
                    Color = bar.Color * frame.Brightness,
                    Height = bar.Height,
                    Width = bar.Width
                }).ToList(),
                Color = frame.Color * frame.Brightness,
                Brightness = frame.Brightness
            };

            RenderSpectrumFrame(fadedFrame);
        }
    }

    private void RenderSpectrumFrame(SpectrumFrame frame)
    {
        if (frame.Bars.Count == 0) return;

        var vertices = new List<float>();

        // Generate vertices for all bars
        foreach (var bar in frame.Bars)
        {
            // Create rectangle for each bar (2 triangles = 6 vertices)
            float left = bar.Position.X;
            float right = bar.Position.X + bar.Width;
            float bottom = bar.Position.Y + bar.Height;
            float top = bar.Position.Y;

            // First triangle (bottom-left, bottom-right, top-left)
            vertices.AddRange(new[] { left, bottom, 0.0f, bar.Color.X, bar.Color.Y, bar.Color.Z });
            vertices.AddRange(new[] { right, bottom, 0.0f, bar.Color.X, bar.Color.Y, bar.Color.Z });
            vertices.AddRange(new[] { left, top, 0.0f, bar.Color.X, bar.Color.Y, bar.Color.Z });

            // Second triangle (bottom-right, top-right, top-left)
            vertices.AddRange(new[] { right, bottom, 0.0f, bar.Color.X, bar.Color.Y, bar.Color.Z });
            vertices.AddRange(new[] { right, top, 0.0f, bar.Color.X, bar.Color.Y, bar.Color.Z });
            vertices.AddRange(new[] { left, top, 0.0f, bar.Color.X, bar.Color.Y, bar.Color.Z });
        }

        // Upload vertex data
        GL.BindVertexArray(_vertexArrayObject);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float), vertices.ToArray(), BufferUsageHint.DynamicDraw);

        // Draw all bars
        GL.DrawArrays(PrimitiveType.Triangles, 0, vertices.Count / 6);
    }

    public void RenderConfigGui()
    {
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Spectrum Bars Settings");
        ImGui.Separator();

        int barCount = _config.BarCount;
        if (ImGui.DragInt("Bar Count", ref barCount, 1.0f, 128, 2048))
        {
            _config.BarCount = barCount;
        }

        ImGui.TextDisabled("Width: Full screen (auto-calculated)");
        ImGui.TextDisabled("Spacing: None (seamless bars)");

        float maxHeight = _config.MaxBarHeight;
        if (ImGui.DragFloat("Max Bar Height", ref maxHeight, 1.0f, 50.0f, 1000.0f))
            _config.MaxBarHeight = maxHeight;

        float sensitivity = _config.Sensitivity;
        if (ImGui.DragFloat("Audio Sensitivity", ref sensitivity, 1000.0f, 1000.0f, 200000.0f))
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
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Frequency Range");
        ImGui.Separator();

        bool useLogScale = _config.UseLogarithmicScale;
        if (ImGui.Checkbox("Logarithmic Scale", ref useLogScale))
            _config.UseLogarithmicScale = useLogScale;

        float minFreq = _config.MinFrequency;
        if (ImGui.DragFloat("Min Frequency (Hz)", ref minFreq, 1.0f, 1.0f, 1000.0f))
            _config.MinFrequency = minFreq;

        float maxFreq = _config.MaxFrequency;
        if (ImGui.DragFloat("Max Frequency (Hz)", ref maxFreq, 100.0f, 1000.0f, 22050.0f))
            _config.MaxFrequency = maxFreq;

        if (!useLogScale)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1.0f, 1.0f, 0.0f, 1.0f), "Note: Linear mode shows 0-22kHz evenly");
            ImGui.TextColored(new System.Numerics.Vector4(1.0f, 1.0f, 0.0f, 1.0f), "Most music energy is in low frequencies (<4kHz)");
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

        bool useRealTimeColor = _config.UseRealTimeColor;
        if (ImGui.Checkbox("Time-based RGB", ref useRealTimeColor))
        {
            _config.UseRealTimeColor = useRealTimeColor;
            if (useRealTimeColor) _config.UseTimeColor = false;
        }

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

    public void LoadConfiguration(string json)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                Converters = { new Vector3JsonConverter() }
            };
            var config = JsonSerializer.Deserialize<SpectrumBarsConfig>(json, options);
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
        _config = new SpectrumBarsConfig();
        // Set positions for full-width at bottom of screen
        _config.PositionX = 0; // Left edge
        _config.PositionY = _currentWindowSize.Y; // Bottom edge

        // Bar width is always auto-calculated from screen size

        IsEnabled = _config.Enabled;
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
