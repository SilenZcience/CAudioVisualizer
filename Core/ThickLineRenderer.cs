using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Runtime.InteropServices;

namespace CAudioVisualizer.Core;

public enum ThickLineMode
{
    Lines,
    LineStrip,
    LineLoop
}

public sealed class ThickLineRenderer
{
    public static readonly ThickLineRenderer Instance = new();

    private const int SrcFloats = 7;   // position (3) + color (4)
    private const int ExpFloats = 8;   // + signed distance from the line centre

    private const float MiterLimit = 4.0f;

    private bool _initialized;
    private int _vbo;
    private int _vaoNative;
    private int _vaoExpanded;
    private int _shaderProgram;
    private int _projectionLocation;
    private int _outerLocation;
    private int _featherLocation;
    private int _alphaLocation;

    private float _maxLineWidth = 1.0f;       // GL_ALIASED_LINE_WIDTH_RANGE max
    private float _maxSmoothLineWidth = 1.0f; // aliased max clamped to GL_SMOOTH_LINE_WIDTH_RANGE max

    private float[] _expanded = Array.Empty<float>();
    private int _expandedCount;
    private Vector2[] _inOffset = Array.Empty<Vector2>();
    private Vector2[] _outOffset = Array.Empty<Vector2>();

    // Scratch buffers for ExpandPolyline, reused every call to keep the hot path
    // allocation-free.
    private Vector2[] _posScratch = Array.Empty<Vector2>();
    private Vector2[] _dInScratch = Array.Empty<Vector2>();
    private Vector2[] _dOutScratch = Array.Empty<Vector2>();

    private ThickLineRenderer() { }

    public void Draw(ThickLineMode mode, List<float> vertices, float width, bool smooth, ref Matrix4 projection)
    {
        int count = vertices.Count / SrcFloats;
        if (count < 2) return;
        if (mode == ThickLineMode.LineLoop && count < 3) return;
        if (width < 1.0f) width = 1.0f;

        EnsureInitialized();

        GL.UseProgram(_shaderProgram);
        GL.UniformMatrix4(_projectionLocation, false, ref projection);
        GL.Uniform1(_alphaLocation, 1.0f);

        // Never trust the reported line width range for widths > 1: some drivers
        // (e.g. Mesa on Intel) report a max of 7.375 but render every GL line at 1px.
        // Only the spec-guaranteed 1px goes down the native path.
        if (width <= 1.0f)
        {
            DrawNative(mode, vertices, count, width, smooth);
        }
        else
        {
            DrawExpanded(mode, vertices, count, width, smooth);
        }
    }

    private void DrawNative(ThickLineMode mode, List<float> vertices, int count, float width, bool smooth)
    {
        GL.BindVertexArray(_vaoNative);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        var span = CollectionsMarshal.AsSpan(vertices);
        GL.BufferData(BufferTarget.ArrayBuffer, span.Length * sizeof(float), ref span[0], BufferUsageHint.DynamicDraw);

        if (smooth)
        {
            GL.Enable(EnableCap.LineSmooth);
            GL.Hint(HintTarget.LineSmoothHint, HintMode.Nicest);
        }

        GL.LineWidth(1.0f);
        GL.DrawArrays(ToPrimitive(mode), 0, count);

        if (smooth) GL.Disable(EnableCap.LineSmooth);
    }

    private void DrawExpanded(ThickLineMode mode, List<float> vertices, int count, float width, bool smooth)
    {
        float half = width * 0.5f;
        float outer = smooth ? half + 0.5f : half;
        float feather = smooth ? 1.0f : 0.0f;

        Expand(mode, vertices, count, outer, smooth);
        if (_expandedCount == 0) return;

        // The feathered edge only works when blending is on; trail renderers disable it
        // when they finish, so make sure it is on for this draw and restore it after.
        bool blendWasEnabled = GL.IsEnabled(EnableCap.Blend);
        GL.Enable(EnableCap.Blend);

        GL.BindVertexArray(_vaoExpanded);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, _expandedCount * sizeof(float), _expanded, BufferUsageHint.DynamicDraw);
        GL.Uniform1(_outerLocation, outer);
        GL.Uniform1(_featherLocation, feather);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _expandedCount / ExpFloats);

        if (!blendWasEnabled) GL.Disable(EnableCap.Blend);
    }

    private void Expand(ThickLineMode mode, List<float> vertices, int count, float outer, bool smooth)
    {
        _expandedCount = 0;
        switch (mode)
        {
            case ThickLineMode.Lines:
                ExpandSegments(vertices, count, outer, smooth);
                break;
            case ThickLineMode.LineStrip:
                ExpandPolyline(vertices, count, outer, smooth, false);
                break;
            case ThickLineMode.LineLoop:
                ExpandPolyline(vertices, count, outer, smooth, true);
                break;
        }
    }

    public sealed class Geometry : IDisposable
    {
        internal int Vao;
        internal int Vbo;
        internal int VertexCount;
        internal float Outer;
        internal float Feather;

        public void Dispose()
        {
            if (Vao != 0)
            {
                GL.DeleteVertexArray(Vao);
                GL.DeleteBuffer(Vbo);
                Vao = 0;
                Vbo = 0;
                VertexCount = 0;
            }
            GC.SuppressFinalize(this);
        }
    }

    public Geometry BuildGeometry(ThickLineMode mode, List<float> vertices, float width, bool smooth)
    {
        var geometry = new Geometry();

        int count = vertices.Count / SrcFloats;
        if (count < 2) return geometry;
        if (mode == ThickLineMode.LineLoop && count < 3) return geometry;
        if (width < 1.0f) width = 1.0f;

        EnsureInitialized();

        float half = width * 0.5f;
        geometry.Outer = smooth ? half + 0.5f : half;
        geometry.Feather = smooth ? 1.0f : 0.0f;

        Expand(mode, vertices, count, geometry.Outer, smooth);
        if (_expandedCount == 0) return geometry;

        geometry.Vbo = GL.GenBuffer();
        geometry.Vao = GL.GenVertexArray();
        GL.BindVertexArray(geometry.Vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, geometry.Vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, _expandedCount * sizeof(float), _expanded, BufferUsageHint.StaticDraw);
        ConfigureExpandedAttribs();
        geometry.VertexCount = _expandedCount / ExpFloats;

        return geometry;
    }

    public void DrawGeometry(Geometry geometry, float alpha, ref Matrix4 projection)
    {
        if (geometry == null || geometry.VertexCount == 0) return;

        EnsureInitialized();

        GL.UseProgram(_shaderProgram);
        GL.UniformMatrix4(_projectionLocation, false, ref projection);
        GL.Uniform1(_alphaLocation, alpha);
        GL.Uniform1(_outerLocation, geometry.Outer);
        GL.Uniform1(_featherLocation, geometry.Feather);

        GL.BindVertexArray(geometry.Vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, geometry.VertexCount);
    }

    // --- geometry expansion -------------------------------------------------

    private void ExpandSegments(List<float> src, int count, float outer, bool smooth)
    {
        int fanSegs = FanSegments(outer);
        for (int i = 0; i + 1 < count; i += 2)
        {
            Vector2 a = GetPos(src, i);
            Vector2 b = GetPos(src, i + 1);
            Vector2 d = b - a;
            float lenSq = d.LengthSquared;

            if (lenSq < 1e-12f)
            {
                // Degenerate segment: native smooth lines render a dot, aliased nothing.
                if (smooth) EmitDisc(src, i, a, outer, fanSegs);
                continue;
            }

            Vector2 dir = d / MathF.Sqrt(lenSq);
            Vector2 n = new Vector2(dir.Y, -dir.X) * outer; // perpendicular, "left" side

            // Quad (two triangles), side = signed perpendicular distance for edge feathering.
            AddVertex(src, i, a.X + n.X, a.Y + n.Y, outer);
            AddVertex(src, i + 1, b.X + n.X, b.Y + n.Y, outer);
            AddVertex(src, i + 1, b.X - n.X, b.Y - n.Y, -outer);

            AddVertex(src, i, a.X + n.X, a.Y + n.Y, outer);
            AddVertex(src, i + 1, b.X - n.X, b.Y - n.Y, -outer);
            AddVertex(src, i, a.X - n.X, a.Y - n.Y, -outer);

            if (smooth)
            {
                EmitCap(src, i, a, -dir, outer, fanSegs);
                EmitCap(src, i + 1, b, dir, outer, fanSegs);
            }
        }
    }

    private void ExpandPolyline(List<float> src, int count, float outer, bool smooth, bool closed)
    {
        if (closed && count < 3) return;
        int segments = closed ? count : count - 1;

        if (_posScratch.Length < count)
        {
            _posScratch = new Vector2[count];
            _dInScratch = new Vector2[count];
            _dOutScratch = new Vector2[count];
            _inOffset = new Vector2[count];
            _outOffset = new Vector2[count];
        }
        Vector2[] pos = _posScratch;
        Vector2[] dIn = _dInScratch;
        Vector2[] dOut = _dOutScratch;

        // Positions once (List indexing with stride 7 is relatively expensive).
        for (int i = 0; i < count; i++)
            pos[i] = GetPos(src, i);

        for (int i = 0; i < count; i++)
        {
            if (closed)
            {
                dIn[i] = Normalized(pos, count, i, i - 1);
                dOut[i] = Normalized(pos, count, i + 1, i);
            }
            else
            {
                dIn[i] = i > 0 ? Normalized(pos, count, i, i - 1) : default;
                dOut[i] = i < count - 1 ? Normalized(pos, count, i + 1, i) : default;
            }
        }

        for (int i = 0; i < count; i++)
        {
            bool hasIn = closed || i > 0;
            bool hasOut = closed || i < count - 1;

            Vector2 inM = default;
            Vector2 outM = default;
            bool mitre = false;

            if (hasIn && hasOut)
            {
                Vector2 n1 = Perp(dIn[i]);
                Vector2 n2 = Perp(dOut[i]);
                Vector2 bis = n1 + n2;
                float bisLenSq = bis.LengthSquared;

                if (bisLenSq >= 1e-8f)
                {
                    bis /= MathF.Sqrt(bisLenSq);
                    float cosHalf = Vector2.Dot(bis, n2); // cos(turn / 2), always > 0 here
                    if (cosHalf >= 1.0f / MiterLimit)
                    {
                        inM = outM = bis * (outer / cosHalf);
                        mitre = true;
                    }
                }
            }

            if (!mitre)
            {
                if (hasIn) inM = Perp(dIn[i]) * outer;
                if (hasOut) outM = Perp(dOut[i]) * outer;
            }

            _inOffset[i] = inM;
            _outOffset[i] = outM;
        }

        for (int i = 0; i < segments; i++)
        {
            int j = (i + 1) % count;
            Vector2 pIn = pos[i];
            Vector2 pOut = pos[j];
            Vector2 si = _outOffset[i];
            Vector2 sj = _inOffset[j];

            Vector2 li = pIn + si, lj = pOut + sj;
            Vector2 ri = pIn - si, rj = pOut - sj;

            int oi = i * SrcFloats;
            int oj = j * SrcFloats;
            float zi = src[oi + 2], ir = src[oi + 3], ig = src[oi + 4], ib = src[oi + 5], ia = src[oi + 6];
            float zj = src[oj + 2], jr = src[oj + 3], jg = src[oj + 4], jb = src[oj + 5], ja = src[oj + 6];

            AddVertexF(li.X, li.Y, zi, ir, ig, ib, ia, outer);
            AddVertexF(lj.X, lj.Y, zj, jr, jg, jb, ja, outer);
            AddVertexF(rj.X, rj.Y, zj, jr, jg, jb, ja, -outer);

            AddVertexF(li.X, li.Y, zi, ir, ig, ib, ia, outer);
            AddVertexF(rj.X, rj.Y, zj, jr, jg, jb, ja, -outer);
            AddVertexF(ri.X, ri.Y, zi, ir, ig, ib, ia, -outer);
        }

        // Fill the outer gap left by bevelled corners (incoming and outgoing offsets
        // differ). Straight passages compare equal and are skipped.
        for (int i = 0; i < count; i++)
        {
            if (!closed && (i == 0 || i == count - 1)) continue;

            Vector2 inM = _inOffset[i];
            Vector2 outM = _outOffset[i];
            // Epsilon, not == : two independently normalized directions for a straight
            // passage can differ by an ulp, which would emit a degenerate sliver.
            if (MathF.Abs(inM.X - outM.X) < 1e-5f && MathF.Abs(inM.Y - outM.Y) < 1e-5f) continue;

            Vector2 p = pos[i];
            int o = i * SrcFloats;
            float z = src[o + 2], r = src[o + 3], g = src[o + 4], b = src[o + 5], a = src[o + 6];

            AddVertexF(p.X + inM.X, p.Y + inM.Y, z, r, g, b, a, outer);
            AddVertexF(p.X, p.Y, z, r, g, b, a, 0.0f);
            AddVertexF(p.X + outM.X, p.Y + outM.Y, z, r, g, b, a, outer);
        }

        if (!closed && smooth)
        {
            int fanSegs = FanSegments(outer);
            Vector2 first = pos[0];
            Vector2 second = pos[1];
            EmitCap(src, 0, first, NormalizeOr(first - second, new Vector2(-1, 0)), outer, fanSegs);

            Vector2 last = pos[count - 1];
            Vector2 beforeLast = pos[count - 2];
            EmitCap(src, count - 1, last, NormalizeOr(last - beforeLast, new Vector2(1, 0)), outer, fanSegs);
        }
    }

    private static Vector2 Normalized(Vector2[] pos, int count, int to, int from)
    {
        if (to < 0) to += count;
        else if (to >= count) to -= count;
        if (from < 0) from += count;
        else if (from >= count) from -= count;

        Vector2 d = pos[to] - pos[from];
        float lenSq = d.LengthSquared;
        return lenSq > 1e-12f ? d / MathF.Sqrt(lenSq) : new Vector2(1, 0);
    }

    private void EmitCap(List<float> src, int colorIndex, Vector2 center, Vector2 outDir, float outer, int segs)
    {
        float baseAngle = MathF.Atan2(outDir.Y, outDir.X) - MathF.PI * 0.5f;
        Vector2 prev = new Vector2(
            center.X + MathF.Cos(baseAngle) * outer,
            center.Y + MathF.Sin(baseAngle) * outer);

        for (int j = 1; j <= segs; j++)
        {
            float ang = baseAngle + MathF.PI * j / segs;
            Vector2 next = new Vector2(
                center.X + MathF.Cos(ang) * outer,
                center.Y + MathF.Sin(ang) * outer);

            AddVertex(src, colorIndex, center.X, center.Y, 0.0f);
            AddVertex(src, colorIndex, prev.X, prev.Y, outer);
            AddVertex(src, colorIndex, next.X, next.Y, outer);

            prev = next;
        }
    }

    private void EmitDisc(List<float> src, int colorIndex, Vector2 center, float outer, int segs)
    {
        EmitCap(src, colorIndex, center, new Vector2(1, 0), outer, segs);
        EmitCap(src, colorIndex, center, new Vector2(-1, 0), outer, segs);
    }

    private static int FanSegments(float outer)
    {
        float step = 2.0f * MathF.Asin(Math.Clamp(0.5f / outer, 0.05f, 1.0f));
        return Math.Clamp((int)MathF.Ceiling(MathF.PI / step), 4, 32);
    }

    private static Vector2 GetPos(List<float> src, int i)
    {
        int o = i * SrcFloats;
        return new Vector2(src[o], src[o + 1]);
    }

    private static Vector2 Perp(Vector2 v) => new Vector2(v.Y, -v.X);

    private static Vector2 NormalizeOr(Vector2 v, Vector2 fallback)
    {
        float lenSq = v.LengthSquared;
        return lenSq > 1e-12f ? v / MathF.Sqrt(lenSq) : fallback;
    }

    private void AddVertex(List<float> src, int vertexIndex, float x, float y, float side)
    {
        EnsureExpandedCapacity(ExpFloats);
        int o = vertexIndex * SrcFloats;
        int d = _expandedCount;
        _expanded[d] = x;
        _expanded[d + 1] = y;
        _expanded[d + 2] = src[o + 2];
        _expanded[d + 3] = src[o + 3];
        _expanded[d + 4] = src[o + 4];
        _expanded[d + 5] = src[o + 5];
        _expanded[d + 6] = src[o + 6];
        _expanded[d + 7] = side;
        _expandedCount += ExpFloats;
    }

    private void AddVertexF(float x, float y, float z, float r, float g, float b, float a, float side)
    {
        EnsureExpandedCapacity(ExpFloats);
        int d = _expandedCount;
        _expanded[d] = x;
        _expanded[d + 1] = y;
        _expanded[d + 2] = z;
        _expanded[d + 3] = r;
        _expanded[d + 4] = g;
        _expanded[d + 5] = b;
        _expanded[d + 6] = a;
        _expanded[d + 7] = side;
        _expandedCount += ExpFloats;
    }

    private void EnsureExpandedCapacity(int additional)
    {
        int required = _expandedCount + additional;
        if (required <= _expanded.Length) return;

        int newSize = Math.Max(required, Math.Max(256, _expanded.Length * 2));
        Array.Resize(ref _expanded, newSize);
    }

    private static PrimitiveType ToPrimitive(ThickLineMode mode) => mode switch
    {
        ThickLineMode.Lines => PrimitiveType.Lines,
        ThickLineMode.LineStrip => PrimitiveType.LineStrip,
        _ => PrimitiveType.LineLoop
    };

    // --- GL setup -----------------------------------------------------------

    private void EnsureInitialized()
    {
        if (_initialized) return;

        float[] range = new float[2];
        GL.GetFloat(GetPName.AliasedLineWidthRange, range);
        _maxLineWidth = range[1];
        GL.GetFloat(GetPName.SmoothLineWidthRange, range);
        _maxSmoothLineWidth = Math.Min(_maxLineWidth, range[1]);

        int prevVao = GL.GetInteger(GetPName.VertexArrayBinding);
        int prevBuffer = GL.GetInteger(GetPName.ArrayBufferBinding);

        _vbo = GL.GenBuffer();
        _vaoNative = CreateVertexArray(withSide: false);
        _vaoExpanded = CreateVertexArray(withSide: true);
        CreateShaderProgram();

        GL.BindVertexArray(prevVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, prevBuffer);

        Console.WriteLine(
            $"ThickLineRenderer: driver reports aliased line width up to {_maxLineWidth}px, " +
            $"smooth up to {_maxSmoothLineWidth}px (only 1px is trusted; wider lines are expanded)");

        _initialized = true;
    }

    private int CreateVertexArray(bool withSide)
    {
        int vao = GL.GenVertexArray();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);

        if (withSide)
        {
            ConfigureExpandedAttribs();
        }
        else
        {
            int stride = SrcFloats * sizeof(float);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(1);
        }

        return vao;
    }

    private static void ConfigureExpandedAttribs()
    {
        int stride = ExpFloats * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, 7 * sizeof(float));
        GL.EnableVertexAttribArray(2);
    }

    private void CreateShaderProgram()
    {
        const string vertexShaderSource = @"
            #version 330 core
            layout(location = 0) in vec3 aPosition;
            layout(location = 1) in vec4 aColor;
            layout(location = 2) in float aSide;

            uniform mat4 projection;
            out vec4 vertexColor;
            out float side;

            void main()
            {
                gl_Position = projection * vec4(aPosition, 1.0);
                vertexColor = aColor;
                side = aSide;
            }";

        const string fragmentShaderSource = @"
            #version 330 core
            in vec4 vertexColor;
            in float side;

            uniform float uOuter;    // expanded half width, in pixels
            uniform float uFeather;  // feather width, in pixels (0 = hard edges)
            uniform float uAlpha;    // extra fade alpha (trail frames)

            out vec4 FragColor;

            void main()
            {
                float alpha = vertexColor.a * uAlpha;
                if (uFeather > 0.0)
                    alpha *= clamp((uOuter - abs(side)) / uFeather, 0.0, 1.0);
                FragColor = vec4(vertexColor.rgb, alpha);
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

        GL.GetProgram(_shaderProgram, GetProgramParameterName.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
            Console.WriteLine($"ThickLineRenderer link error: {GL.GetProgramInfoLog(_shaderProgram)}");

        _projectionLocation = GL.GetUniformLocation(_shaderProgram, "projection");
        _outerLocation = GL.GetUniformLocation(_shaderProgram, "uOuter");
        _featherLocation = GL.GetUniformLocation(_shaderProgram, "uFeather");
        _alphaLocation = GL.GetUniformLocation(_shaderProgram, "uAlpha");

        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
    }
}
