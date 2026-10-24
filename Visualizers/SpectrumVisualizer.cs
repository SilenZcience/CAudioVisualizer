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
    public float Amplitude { get; set; } = 30f;
    public int BarCount { get; set; } = 64;
    public float BarWidth { get; set; } = 8.0f;
    public float BarSpacing { get; set; } = 2.0f;
    public float Size { get; set; } = 200.0f;
    public int BarAngle { get; set; } = 0; // 0 = linear, 360 = full circle
    public int Rotation { get; set; } = 0; // rotation of the whole arrangement, 0-360 deg
    public bool EnablePeakIndicators { get; set; } = true;
    public float PeakDropSpeed { get; set; } = 5f;
    public float PeakLength { get; set; } = 8.0f;
    public float PeakOffset { get; set; } = 5.0f;
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
    public ThickLineRenderer.Geometry? Geometry;
}

public class SpectrumVisualizer : IVisualizer, IConfigurable
{
    public bool IsEnabled
    {
        get => _config.Enabled;
        set => _config.Enabled = value;
    }

    private SpectrumConfig _config = new();
    private bool _initialized = false;
    private float[] _audioData = Array.Empty<float>();
    private float[] _fftData = Array.Empty<float>();
    private readonly List<SpectrumFrame> _trailFrames = new();
    private readonly List<float> _mainVertexBuffer = new();
    private VisualizerManager? _visualizerManager;

    private Vector2i CurrentWindowSize => _visualizerManager?.GetCurrentWindowSize() ?? new Vector2i(800, 600);

    private float[] _peakPositions = Array.Empty<float>();

    public void SetVisualizerManager(VisualizerManager manager)
    {
        _visualizerManager = manager;
    }

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
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

        if (_config.EnableFadeTrail)
        {
            UpdateTrailFrames();
            RenderTrailFrames(ref projection);
        }
        else
        {
            // Drop leftover trail frames (and their GPU geometry) when the trail is off.
            ClearTrailFrames();

            var currentFrame = GenerateCurrentSpectrum(copyVertices: false);
            if (currentFrame.Vertices.Count == 0) return;

            Vector3 color = _config.UseTimeColor ? TimeColorHelper.GetTimeBasedColor() :
                           _config.UseRealTimeColor ? TimeColorHelper.GetRealTimeBasedColor() : _config.Color;
            if (_config.InvertColor) color = TimeColorHelper.InvertColor(color);

            RenderSpectrum(currentFrame.Vertices, currentFrame.BarWidth, color, ref projection);
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
                frame.Geometry?.Dispose();
                _trailFrames.RemoveAt(i);
            }
            else
            {
                _trailFrames[i] = frame;
            }
        }
    }

    private void RenderTrailFrames(ref Matrix4 projection)
    {
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // Each frame's geometry is expanded and uploaded once, then reused; the fade
        // alpha is applied as a uniform, so no per-frame re-upload happens here.
        for (int i = _trailFrames.Count - 1; i >= 0; i--)
        {
            var frame = _trailFrames[i];
            if (frame.Vertices.Count == 0) continue;

            if (frame.Geometry == null)
            {
                frame.Geometry = ThickLineRenderer.Instance.BuildGeometry(
                    ThickLineMode.Lines, frame.Vertices, frame.BarWidth, smooth: true);
                _trailFrames[i] = frame;
            }

            ThickLineRenderer.Instance.DrawGeometry(frame.Geometry, frame.Alpha, ref projection);
        }

        GL.Disable(EnableCap.Blend);
    }

    private void ClearTrailFrames()
    {
        if (_trailFrames.Count == 0) return;

        foreach (var frame in _trailFrames)
            frame.Geometry?.Dispose();
        _trailFrames.Clear();
    }

    private SpectrumFrame GenerateCurrentSpectrum(bool copyVertices = true)
    {
        float[] dataSource = _config.UseFFT ? _fftData : _audioData;
        _mainVertexBuffer.Clear();
        if (dataSource.Length == 0)
        {
            return new SpectrumFrame
            {
                Vertices = copyVertices ? new List<float>() : _mainVertexBuffer,
                Color = _config.Color,
                Alpha = 1.0f,
                BarWidth = _config.BarWidth
            };
        }
        if (!_config.UseFFT && dataSource.Length > 1 && _config.BarAngle > 330)
        {
            int smoothCount = Math.Max(2, (int)(dataSource.Length * 0.1f));
            float first = dataSource[0];
            float last = dataSource[dataSource.Length - 1];
            // Blend last smoothCount bars into first value
            for (int j = 0; j < smoothCount; j++)
            {
                float t = (float)j / (smoothCount - 1);
                float blend = (1 - MathF.Cos(MathF.PI * t)) / 2f;
                float fakeValue = last * (1 - blend) + first * blend;
                float weight = 1f - t; // Near seam: more fake, far: more original
                dataSource[dataSource.Length - 1 - j] = dataSource[dataSource.Length - 1 - j] * (1 - weight) + fakeValue * weight;
            }
            // Blend first smoothCount bars into last value
            for (int j = 0; j < smoothCount; j++)
            {
                float t = (float)j / (smoothCount - 1);
                float blend = (1 - MathF.Cos(MathF.PI * (1 - t))) / 2f;
                float fakeValue = first * (1 - blend) + last * blend;
                float weight = 1f - t;
                dataSource[j] = dataSource[j] * (1 - weight) + fakeValue * weight;
            }
        }

        int count = _config.BarCount;
        float centerX = _config.PositionX;
        float centerY = _config.PositionY;

        Vector3 color = _config.UseTimeColor ? TimeColorHelper.GetTimeBasedColor() :
                       _config.UseRealTimeColor ? TimeColorHelper.GetRealTimeBasedColor() : _config.Color;
        if (_config.InvertColor) color = TimeColorHelper.InvertColor(color);

        var distributedSize = _config.Size / (count * 2);
        var adjustedBarWidth = _config.BarWidth + _config.BarWidth * distributedSize;
        var adjustedBarSpacing = _config.BarSpacing + _config.BarSpacing * distributedSize;

        float w = count * (adjustedBarWidth + adjustedBarSpacing);
        float arcAngle = _config.BarAngle / 180f * MathF.PI;
        float r = w / arcAngle;
        Vector2 center = new Vector2(centerX, centerY + r);
        float thetaStart = MathF.PI / 2 + arcAngle / 2;

        bool rotated = _config.Rotation != 0;
        float rotCos = 1.0f, rotSin = 0.0f;
        Vector2 pivot = new Vector2(centerX, centerY);
        if (rotated)
        {
            float rotRad = _config.Rotation / 180f * MathF.PI;
            rotCos = MathF.Cos(rotRad);
            rotSin = MathF.Sin(rotRad);
            if (arcAngle >= 1e-3f) // linear row keeps the anchor as pivot (r -> inf)
                pivot.Y += r * Math.Min(arcAngle / (2.0f * MathF.PI), 1.0f);
        }

        if (_peakPositions.Length != count)
        {
            Array.Resize(ref _peakPositions, count);
        }

        for (int i = 0; i < count; i++)
        {
            int fftIndex = (int)(i * (dataSource.Length / (float)count));
            float value = dataSource[fftIndex] * _config.Amplitude;

            if (_config.EnablePeakIndicators)
            {
                _peakPositions[i] -= _config.PeakDropSpeed;
                _peakPositions[i] = Math.Max(_peakPositions[i], value);
                _peakPositions[i] = Math.Clamp(_peakPositions[i], 0, Math.Max(CurrentWindowSize.Y*2, CurrentWindowSize.X));
            }

            float theta = thetaStart - arcAngle * i / count;

            Vector2 start = arcAngle < 1e-3f
                ? new Vector2(centerX - w / 2 + (float)i / (count - 1) * w, centerY)
                : new Vector2(center.X + r * MathF.Cos(theta), center.Y - r * MathF.Sin(theta));
            Vector2 dir = Vector2.Normalize(new Vector2(MathF.Cos(theta), -MathF.Sin(theta)));

            if (rotated)
            {
                float dx = start.X - pivot.X;
                float dy = start.Y - pivot.Y;
                start.X = pivot.X + dx * rotCos - dy * rotSin;
                start.Y = pivot.Y + dx * rotSin + dy * rotCos;
                float dirX = dir.X * rotCos - dir.Y * rotSin;
                dir.Y = dir.X * rotSin + dir.Y * rotCos;
                dir.X = dirX;
            }

            Vector2 end = start + dir * value;

            // Bar vertices
            _mainVertexBuffer.Add(start.X);
            _mainVertexBuffer.Add(start.Y);
            _mainVertexBuffer.Add(0.0f);
            _mainVertexBuffer.Add(color.X);
            _mainVertexBuffer.Add(color.Y);
            _mainVertexBuffer.Add(color.Z);
            _mainVertexBuffer.Add(1.0f);
            _mainVertexBuffer.Add(end.X);
            _mainVertexBuffer.Add(end.Y);
            _mainVertexBuffer.Add(0.0f);
            _mainVertexBuffer.Add(color.X);
            _mainVertexBuffer.Add(color.Y);
            _mainVertexBuffer.Add(color.Z);
            _mainVertexBuffer.Add(1.0f);

            if (_config.EnablePeakIndicators)
            {
                Vector2 lineStart = start + dir * (_peakPositions[i] + _config.PeakOffset);
                Vector2 lineEnd = lineStart + dir * _config.PeakLength;

                _mainVertexBuffer.Add(lineStart.X);
                _mainVertexBuffer.Add(lineStart.Y);
                _mainVertexBuffer.Add(0.0f);
                _mainVertexBuffer.Add(color.X);
                _mainVertexBuffer.Add(color.Y);
                _mainVertexBuffer.Add(color.Z);
                _mainVertexBuffer.Add(1.0f);

                _mainVertexBuffer.Add(lineEnd.X);
                _mainVertexBuffer.Add(lineEnd.Y);
                _mainVertexBuffer.Add(0.0f);
                _mainVertexBuffer.Add(color.X);
                _mainVertexBuffer.Add(color.Y);
                _mainVertexBuffer.Add(color.Z);
                _mainVertexBuffer.Add(1.0f);
            }
        }

        return new SpectrumFrame
        {
            Vertices = copyVertices ? new List<float>(_mainVertexBuffer) : _mainVertexBuffer,
            Color = color,
            Alpha = 1.0f,
            BarWidth = adjustedBarWidth
        };
    }

    private void RenderSpectrum(List<float> vertices, float barWidth, Vector3 color, ref Matrix4 projection)
    {
        if (vertices.Count == 0) return;

        // Widths > 1 are always expanded into triangles with antialiased edges (the
        // driver-reported width range is not trustworthy).
        ThickLineRenderer.Instance.Draw(ThickLineMode.Lines, vertices, barWidth, true, ref projection);
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
        if (ImGui.SliderInt("Bar Count", ref barCount, 3, 1024))
        {
            _config.BarCount = barCount;
            if (-2.0f * barCount > _config.Size)
                _config.Size = -2.0f * barCount;
        }

        float size = _config.Size;
        if (ImGui.SliderFloat("Size", ref size, -2.0f * _config.BarCount, 500.0f))
            _config.Size = size;

        int barAngle = _config.BarAngle;
        if (ImGui.SliderInt("Bar Angle (deg)", ref barAngle, 0, 360))
            _config.BarAngle = barAngle;

        int rotation = _config.Rotation;
        if (ImGui.SliderInt("Rotation (deg)", ref rotation, 0, 360))
            _config.Rotation = rotation;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Rotates the entire visualizer around its position.");

        float adjustedBarWidth = _config.BarWidth + _config.BarWidth * _config.Size / (_config.BarCount * 2);
        float adjustedBarSpacing = _config.BarSpacing + _config.BarSpacing * _config.Size / (_config.BarCount * 2);
        float w = _config.BarCount * (adjustedBarWidth + adjustedBarSpacing);
        float r = _config.BarAngle > 0 ? w / (_config.BarAngle / 180f * MathF.PI) : 0;
        Vector2 center = new Vector2(_config.PositionX, _config.PositionY + r);
        if (_config.Rotation != 0 && _config.BarAngle > 0)
        {
            float rotRad = _config.Rotation / 180f * MathF.PI;
            float t = Math.Min((_config.BarAngle / 180f * MathF.PI) / (2.0f * MathF.PI), 1.0f);
            Vector2 pivot = new Vector2(_config.PositionX, _config.PositionY + r * t);
            float dx = center.X - pivot.X, dy = center.Y - pivot.Y;
            center.X = pivot.X + dx * MathF.Cos(rotRad) - dy * MathF.Sin(rotRad);
            center.Y = pivot.Y + dx * MathF.Sin(rotRad) + dy * MathF.Cos(rotRad);
        }

        ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1.0f), $"Calculated Geometry:");
        ImGui.Text($"width:  {w:F2}");
        ImGui.Text($"radius: {r:F2}");
        ImGui.Text($"center: ({center.X:F2} / {center.Y:F2})");

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Peak Indicators");
        ImGui.Separator();

        bool enablePeakIndicators = _config.EnablePeakIndicators;
        if (ImGui.Checkbox("Enable Peak Indicators", ref enablePeakIndicators))
            _config.EnablePeakIndicators = enablePeakIndicators;

        if (_config.EnablePeakIndicators)
        {
            float peakDropSpeed = _config.PeakDropSpeed;
            if (ImGui.SliderFloat("Peak Drop Speed", ref peakDropSpeed, 0.01f, 100.0f))
                _config.PeakDropSpeed = peakDropSpeed;

            float peakLength = _config.PeakLength;
            if (ImGui.SliderFloat("Peak Length", ref peakLength, 1.0f, 50.0f))
                _config.PeakLength = peakLength;

            float peakOffset = _config.PeakOffset;
            if (ImGui.SliderFloat("Peak Offset", ref peakOffset, 0.0f, 50.0f))
                _config.PeakOffset = peakOffset;
        }

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
        ClearTrailFrames();
        _initialized = false;
    }
}
