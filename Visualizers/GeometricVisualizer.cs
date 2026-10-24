using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Text.Json;
using ImGuiNET;
using CAudioVisualizer.Core;

namespace CAudioVisualizer.Visualizers;

public class GeometricConfig
{
    public bool Enabled { get; set; } = true;
    public Vector3 Color { get; set; } = new Vector3(0.0f, 0.8f, 1.0f);
    public int PositionX { get; set; } = -1;
    public int PositionY { get; set; } = -1;

    public int GeometryMode { get; set; } = 2; // 0=Sphere, 1=Spiky Ball, 2=All

    public float SphereRadius { get; set; } = 140.0f;
    public int SphereSegments { get; set; } = 24;
    public bool ShowSphere { get; set; } = true;

    public int SpikyBallPoints { get; set; } = 64;
    public float SpikyBallRadius { get; set; } = 140.0f;
    public float SpikyBallSpikeLength { get; set; } = 90.0f;
    public bool ShowSpikyBall { get; set; } = true;

    public float RotationSpeed { get; set; } = 1.5f;
    public float RotationAudioStrength { get; set; } = 1.0f;

    public float EnergySensitivity { get; set; } = 4.0f;
    public float PulseAudioStrength { get; set; } = 1.0f;

    public float LineThickness { get; set; } = 3.0f;
    public float Brightness { get; set; } = 1.2f;
    public bool EnableTrails { get; set; } = true;
    public float TrailFade { get; set; } = 0.82f;
    public int TrailLength { get; set; } = 14;

    public bool UseTimeColor { get; set; } = true;
    public bool UseRealTimeColor { get; set; } = false;
    public bool InvertColor { get; set; } = false;
}

public struct GeometricFrame
{
    public List<float> Vertices;
    public Vector3 Color;
    public float Alpha;
    public float LineWidth;
    public ThickLineRenderer.Geometry? Geometry;
}

public class GeometricVisualizer : IVisualizer, IConfigurable
{
    public bool IsEnabled
    {
        get => _config.Enabled;
        set => _config.Enabled = value;
    }

    private GeometricConfig _config = new();
    private bool _initialized = false;
    private float[] _audioData = Array.Empty<float>();
    private float[] _fftData = Array.Empty<float>();
    private readonly List<GeometricFrame> _trailFrames = new();
    private readonly List<float> _vertexBuffer = new();
    private readonly List<float> _trailVertexBuffer = new();
    private readonly List<Vector3> _spikyDirs = new();
    private VisualizerManager? _visualizerManager;
    private float _visualTime = 0f;
    private float _smoothedEnergy = 0f;

    private Vector2i CurrentWindowSize => _visualizerManager?.GetCurrentWindowSize() ?? new Vector2i(800, 600);

    public void SetVisualizerManager(VisualizerManager manager) => _visualizerManager = manager;

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
    }

    public void Update(float[] audioData, float[] fftData, double deltaTime)
    {
        _audioData = audioData ?? Array.Empty<float>();
        _fftData = fftData ?? Array.Empty<float>();

        // Audio energy from whichever source is stronger.
        float waveformEnergy = GetEnergy(_audioData);
        float fftEnergy = GetEnergy(_fftData);
        float targetEnergy = Math.Max(waveformEnergy, fftEnergy) * _config.EnergySensitivity;

        // Peak-hold envelope: jumps instantly to the beat, decays smoothly back down.
        float decayFactor = (float)Math.Exp(-deltaTime * 5.0);
        _smoothedEnergy = Math.Max(_smoothedEnergy * decayFactor, targetEnergy);
        _smoothedEnergy = Math.Clamp(_smoothedEnergy, 0f, 25f);

        // Rotation time always moves forward. Base speed from slider; audio only ADDS speed
        // (never subtracts), so on a beat it spins faster — never stops or reverses.
        float audioSpeedBoost = _smoothedEnergy * _config.RotationAudioStrength * 10.0f;
        _visualTime += (float)deltaTime * (_config.RotationSpeed + audioSpeedBoost);
    }

    private static float GetEnergy(float[] data)
    {
        if (data == null || data.Length == 0) return 0f;
        int count = Math.Min(data.Length, 512);
        float sum = 0f;
        for (int i = 0; i < count; i++) sum += data[i] * data[i];
        return MathF.Sqrt(sum / count);
    }

    public void Render(Matrix4 projection)
    {
        if (!IsEnabled || !_initialized) return;

        var windowSize = CurrentWindowSize;
        if (_config.PositionX == -1) _config.PositionX = windowSize.X / 2;
        if (_config.PositionY == -1) _config.PositionY = windowSize.Y / 2;

        if (_config.EnableTrails)
        {
            UpdateTrails();
            RenderTrails(ref projection);
        }
        else
        {
            ClearTrails();
            var frame = GenerateFrame(_vertexBuffer);
            if (frame.Vertices.Count == 0) return;
            RenderFrame(frame.Vertices, frame.LineWidth, ref projection);
        }
    }

    private GeometricFrame GenerateFrame(List<float>? reusableBuffer = null)
    {
        var vertices = reusableBuffer ?? new List<float>();
        vertices.Clear();

        float energy = _smoothedEnergy;
        float pulse = 1.0f + energy * 0.08f * _config.PulseAudioStrength;

        float rot = _visualTime;
        var rotMat = Matrix4.CreateRotationY(rot) *
                     Matrix4.CreateRotationX(rot * 0.5f) *
                     Matrix4.CreateRotationZ(rot * 0.25f);
        var m3 = new Matrix3(
            new Vector3(rotMat.Row0.X, rotMat.Row0.Y, rotMat.Row0.Z),
            new Vector3(rotMat.Row1.X, rotMat.Row1.Y, rotMat.Row1.Z),
            new Vector3(rotMat.Row2.X, rotMat.Row2.Y, rotMat.Row2.Z));

        int cx = _config.PositionX;
        int cy = _config.PositionY;
        float persp = 900.0f;

        Vector3 color = _config.UseTimeColor ? TimeColorHelper.GetTimeBasedColor() :
                       _config.UseRealTimeColor ? TimeColorHelper.GetRealTimeBasedColor() : _config.Color;
        if (_config.InvertColor) color = TimeColorHelper.InvertColor(color);
        color *= _config.Brightness;

        if (_config.ShowSphere)
        {
            float radius = _config.SphereRadius * pulse;
            AddSphere(vertices, cx, cy, radius, m3, persp, color, energy);
        }

        if (_config.ShowSpikyBall)
        {
            float sRadius = _config.SpikyBallRadius * pulse;
            float sSpike = _config.SpikyBallSpikeLength * pulse;
            AddSpikyBall(vertices, cx, cy, sRadius, sSpike, m3, persp, color, energy);
        }

        return new GeometricFrame
        {
            Vertices = vertices,
            Color = color,
            Alpha = 1.0f,
            LineWidth = _config.LineThickness
        };
    }

    private void AddSphere(List<float> vertices, int cx, int cy, float radius, Matrix3 rot, float persp, Vector3 color, float energy)
    {
        int segments = Math.Clamp(_config.SphereSegments, 8, 48);
        int rings = segments / 2;

        // Latitude rings
        for (int r = 0; r < rings; r++)
        {
            float phi = (r / (float)(rings - 1)) * MathF.PI;
            float sinPhi = MathF.Sin(phi);
            float cosPhi = MathF.Cos(phi);

            Vector2 prev = default;
            bool hasPrev = false;
            Vector2 first = default;

            for (int s = 0; s <= segments; s++)
            {
                float theta = (s / (float)segments) * MathF.PI * 2.0f;
                Vector3 p3 = new Vector3(sinPhi * MathF.Cos(theta), sinPhi * MathF.Sin(theta), cosPhi) * radius;
                Vector3 p = Vector3.TransformRow(p3, rot);
                float depth = persp + p.Z;
                if (depth < 60f) depth = 60f;
                float scale = persp / depth;
                Vector2 cur = new Vector2(cx + p.X * scale, cy - p.Y * scale);

                if (s == 0) first = cur;
                if (hasPrev) AddLine(vertices, prev, cur, color);
                prev = cur;
                hasPrev = true;
            }
            AddLine(vertices, prev, first, color);
        }

        // Longitude meridians
        for (int s = 0; s <= segments; s += 2)
        {
            float theta = (s / (float)segments) * MathF.PI * 2.0f;

            Vector2 prev = default;
            bool hasPrev = false;

            for (int r = 0; r <= rings; r++)
            {
                float phi = (r / (float)rings) * MathF.PI;
                float sinPhi = MathF.Sin(phi);
                float cosPhi = MathF.Cos(phi);
                Vector3 p3 = new Vector3(sinPhi * MathF.Cos(theta), sinPhi * MathF.Sin(theta), cosPhi) * radius;
                Vector3 p = Vector3.TransformRow(p3, rot);
                float depth = persp + p.Z;
                if (depth < 60f) depth = 60f;
                float scale = persp / depth;
                Vector2 cur = new Vector2(cx + p.X * scale, cy - p.Y * scale);

                if (hasPrev) AddLine(vertices, prev, cur, color);
                prev = cur;
                hasPrev = true;
            }
        }
    }

    private void AddSpikyBall(List<float> vertices, int cx, int cy, float radius, float spikeLength, Matrix3 rot, float persp, Vector3 color, float energy)
    {
        int pointCount = Math.Clamp(_config.SpikyBallPoints, 12, 128);
        int spikeSides = 4; // pyramid spike = square base
        float baseSize = 24.0f;

        float spikeMult = 1.0f + energy * 1.2f * _config.PulseAudioStrength;
        float totalRadius = radius + spikeLength * spikeMult;

        // Distribute spike base points evenly on a sphere (Fibonacci spiral). Cache when count unchanged.
        if (_spikyDirs.Count != pointCount)
        {
            _spikyDirs.Clear();
            for (int i = 0; i < pointCount; i++)
            {
                float phi = MathF.Acos(1.0f - 2.0f * (i + 0.5f) / pointCount);
                float theta = MathF.PI * (1.0f + MathF.Sqrt(5.0f)) * i;
                float sinPhi = MathF.Sin(phi);
                _spikyDirs.Add(new Vector3(
                    sinPhi * MathF.Cos(theta),
                    sinPhi * MathF.Sin(theta),
                    MathF.Cos(phi)
                ));
            }
        }
        var dirs = _spikyDirs;

        // Build each pyramid spike: base polygon on surface + lines to tip.
        for (int i = 0; i < pointCount; i++)
        {
            Vector3 d = dirs[i];

            // Build an orthonormal basis around the spike direction.
            Vector3 up = MathF.Abs(d.Y) < 0.9f ? new Vector3(0, 1, 0) : new Vector3(1, 0, 0);
            Vector3 right = Vector3.Normalize(Vector3.Cross(up, d));
            Vector3 fwd = Vector3.Cross(d, right);

            Vector3 baseCenter = d * radius;
            Vector3 tip = d * totalRadius;

            // Base polygon vertices (on sphere surface).
            Vector2[] basePts = new Vector2[spikeSides];
            for (int s = 0; s < spikeSides; s++)
            {
                float ang = (s / (float)spikeSides) * MathF.PI * 2.0f;
                basePts[s] = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * baseSize;
            }

            // Lines around the base polygon.
            for (int s = 0; s < spikeSides; s++)
            {
                Vector3 p1 = Vector3.TransformRow(baseCenter + right * basePts[s].X + fwd * basePts[s].Y, rot);
                Vector3 p2 = Vector3.TransformRow(baseCenter + right * basePts[(s + 1) % spikeSides].X + fwd * basePts[(s + 1) % spikeSides].Y, rot);
                AddLine3D(vertices, p1, p2, persp, cx, cy, color);
            }

            // Lines from each base vertex to the tip.
            for (int s = 0; s < spikeSides; s++)
            {
                Vector3 p1 = Vector3.TransformRow(baseCenter + right * basePts[s].X + fwd * basePts[s].Y, rot);
                Vector3 p2 = Vector3.TransformRow(tip, rot);
                AddLine3D(vertices, p1, p2, persp, cx, cy, color);
            }
        }
    }

    private void AddLine3D(List<float> vertices, Vector3 a, Vector3 b, float persp, int cx, int cy, Vector3 color)
    {
        float dA = persp + a.Z;
        if (dA < 60f) dA = 60f;
        float sA = persp / dA;
        Vector2 pa = new Vector2(cx + a.X * sA, cy - a.Y * sA);

        float dB = persp + b.Z;
        if (dB < 60f) dB = 60f;
        float sB = persp / dB;
        Vector2 pb = new Vector2(cx + b.X * sB, cy - b.Y * sB);

        AddLine(vertices, pa, pb, color);
    }

    private static void AddLine(List<float> vertices, Vector2 a, Vector2 b, Vector3 color)
    {
        vertices.Add(a.X);
        vertices.Add(a.Y);
        vertices.Add(0.0f);
        vertices.Add(color.X);
        vertices.Add(color.Y);
        vertices.Add(color.Z);
        vertices.Add(1.0f);

        vertices.Add(b.X);
        vertices.Add(b.Y);
        vertices.Add(0.0f);
        vertices.Add(color.X);
        vertices.Add(color.Y);
        vertices.Add(color.Z);
        vertices.Add(1.0f);
    }

    private void RenderFrame(List<float> vertices, float lineWidth, ref Matrix4 projection)
    {
        if (vertices.Count == 0) return;
        ThickLineRenderer.Instance.Draw(ThickLineMode.Lines, vertices, lineWidth, true, ref projection);
    }

    private void UpdateTrails()
    {
        _trailVertexBuffer.Clear();
        var frame = GenerateFrame(_trailVertexBuffer);
        // Store a copy for trail frame (vertices will be owned by the frame; geometry takes a copy)
        _trailFrames.Insert(0, new GeometricFrame
        {
            Vertices = new List<float>(frame.Vertices),
            Color = frame.Color,
            Alpha = 1.0f,
            LineWidth = frame.LineWidth,
            Geometry = null
        });

        for (int i = _trailFrames.Count - 1; i >= 0; i--)
        {
            var f = _trailFrames[i];
            f.Alpha *= _config.TrailFade;
            if (f.Alpha < 0.01f || i >= _config.TrailLength)
            {
                f.Geometry?.Dispose();
                _trailFrames.RemoveAt(i);
            }
            else
            {
                _trailFrames[i] = f;
            }
        }
    }

    private void RenderTrails(ref Matrix4 projection)
    {
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        for (int i = _trailFrames.Count - 1; i >= 0; i--)
        {
            var frame = _trailFrames[i];
            if (frame.Vertices.Count == 0) continue;

            if (frame.Geometry == null)
            {
                frame.Geometry = ThickLineRenderer.Instance.BuildGeometry(
                    ThickLineMode.Lines, frame.Vertices, frame.LineWidth, smooth: true);
                _trailFrames[i] = frame;
            }

            ThickLineRenderer.Instance.DrawGeometry(frame.Geometry, frame.Alpha, ref projection);
        }

        GL.Disable(EnableCap.Blend);
    }

    private void ClearTrails()
    {
        if (_trailFrames.Count == 0) return;
        foreach (var frame in _trailFrames)
            frame.Geometry?.Dispose();
        _trailFrames.Clear();
    }

    public void RenderConfigGui()
    {
        if (!ImGui.CollapsingHeader("Geometric Visualizer", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var ws = CurrentWindowSize;

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Position");
        ImGui.Separator();

        int px = _config.PositionX;
        if (ImGui.SliderInt("Position X", ref px, 0, ws.X)) _config.PositionX = px;
        int py = _config.PositionY;
        if (ImGui.SliderInt("Position Y", ref py, 0, ws.Y)) _config.PositionY = py;

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Geometry");
        ImGui.Separator();

        int mode = _config.GeometryMode;
        if (ImGui.Combo("Geometry Mode", ref mode, "Sphere\0Spiky Ball\0All\0"))
        {
            _config.GeometryMode = mode;
            _config.ShowSphere = mode != 1;
            _config.ShowSpikyBall = mode != 0;
        }

        bool showSphere = _config.ShowSphere;
        if (ImGui.Checkbox("Wireframe Sphere", ref showSphere)) _config.ShowSphere = showSphere;
        float sphereRadius = _config.SphereRadius;
        if (ImGui.SliderFloat("Sphere Radius", ref sphereRadius, 40.0f, 500.0f)) _config.SphereRadius = sphereRadius;
        int sphereSegments = _config.SphereSegments;
        if (ImGui.SliderInt("Sphere Segments", ref sphereSegments, 8, 48)) _config.SphereSegments = sphereSegments;

        bool showSpikyBall = _config.ShowSpikyBall;
        if (ImGui.Checkbox("3D Spiky Ball", ref showSpikyBall)) _config.ShowSpikyBall = showSpikyBall;
        float spikyRadius = _config.SpikyBallRadius;
        if (ImGui.SliderFloat("Spiky Ball Radius", ref spikyRadius, 40.0f, 500.0f)) _config.SpikyBallRadius = spikyRadius;
        float spikySpikeLen = _config.SpikyBallSpikeLength;
        if (ImGui.SliderFloat("Spike Length", ref spikySpikeLen, 10.0f, 300.0f)) _config.SpikyBallSpikeLength = spikySpikeLen;
        int spikyPoints = _config.SpikyBallPoints;
        if (ImGui.SliderInt("Spike Count", ref spikyPoints, 12, 128)) _config.SpikyBallPoints = spikyPoints;

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Motion & Audio Reaction");
        ImGui.Separator();

        float rotSpeed = _config.RotationSpeed;
        if (ImGui.SliderFloat("Rotation Speed", ref rotSpeed, -2.0f, 2.0f)) _config.RotationSpeed = rotSpeed;
        float rotationAudio = _config.RotationAudioStrength;
        if (ImGui.SliderFloat("Rotation Audio Strength", ref rotationAudio, 0.0f, 5.0f)) _config.RotationAudioStrength = rotationAudio;

        float energySens = _config.EnergySensitivity;
        if (ImGui.SliderFloat("Energy Sensitivity", ref energySens, 0.0f, 15.0f)) _config.EnergySensitivity = energySens;
        float pulseStrength = _config.PulseAudioStrength;
        if (ImGui.SliderFloat("Pulse Audio Strength", ref pulseStrength, 0.0f, 5.0f)) _config.PulseAudioStrength = pulseStrength;

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Style");
        ImGui.Separator();

        float thickness = _config.LineThickness;
        if (ImGui.SliderFloat("Line Thickness", ref thickness, 1.0f, 8.0f)) _config.LineThickness = thickness;
        float brightness = _config.Brightness;
        if (ImGui.SliderFloat("Brightness", ref brightness, 0.1f, 3.0f)) _config.Brightness = brightness;

        bool trails = _config.EnableTrails;
        if (ImGui.Checkbox("Trails", ref trails)) _config.EnableTrails = trails;
        if (_config.EnableTrails)
        {
            float trailFade = _config.TrailFade;
            if (ImGui.SliderFloat("Trail Fade", ref trailFade, 0.7f, 0.98f)) _config.TrailFade = trailFade;
            int trailLen = _config.TrailLength;
            if (ImGui.SliderInt("Trail Length", ref trailLen, 5, 40)) _config.TrailLength = trailLen;
        }

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Colors");
        ImGui.Separator();

        bool useTimeColor = _config.UseTimeColor;
        if (ImGui.Checkbox("Rainbow", ref useTimeColor)) { _config.UseTimeColor = useTimeColor; if (useTimeColor) _config.UseRealTimeColor = false; }
        ImGui.SameLine();
        bool useRealTimeColor = _config.UseRealTimeColor;
        if (ImGui.Checkbox("Time-based RGB", ref useRealTimeColor)) { _config.UseRealTimeColor = useRealTimeColor; if (useRealTimeColor) _config.UseTimeColor = false; }
        ImGui.SameLine();
        bool invertColor = _config.InvertColor;
        if (ImGui.Checkbox("Invert", ref invertColor)) _config.InvertColor = invertColor;

        if (!_config.UseTimeColor && !_config.UseRealTimeColor)
        {
            var color = new System.Numerics.Vector3(_config.Color.X, _config.Color.Y, _config.Color.Z);
            if (ImGui.ColorEdit3("Color", ref color)) _config.Color = new Vector3(color.X, color.Y, color.Z);
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
            var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new VectorJsonConverter() } };
            return JsonSerializer.Serialize(_config, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save Geometric config: {ex.Message}");
            return "{}";
        }
    }

    public void LoadConfiguration(string configuration)
    {
        try
        {
            var options = new JsonSerializerOptions { Converters = { new VectorJsonConverter() } };
            var config = JsonSerializer.Deserialize<GeometricConfig>(configuration, options);
            if (config != null)
            {
                _config = config;
                if (_config.GeometryMode < 0 || _config.GeometryMode > 2)
                    _config.GeometryMode = _config.GeometryMode >= 2 ? 2 : 0;
                if (_config.PositionX == 0 && _config.PositionY == 0)
                {
                    var ws = CurrentWindowSize;
                    _config.PositionX = ws.X / 2;
                    _config.PositionY = ws.Y / 2;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load Geometric config: {ex.Message}");
        }
    }

    public void ResetToDefaults()
    {
        _config = new GeometricConfig();
        _config.PositionX = CurrentWindowSize.X / 2;
        _config.PositionY = CurrentWindowSize.Y / 2;
    }

    public void Dispose()
    {
        ClearTrails();
    }
}
