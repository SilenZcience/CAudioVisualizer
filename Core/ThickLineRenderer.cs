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
    private const int ExpandedFloats = 9; // gl_Position (4) + color (4) + side (1)

    private bool _initialized;
    private int _vbo;
    private int _eboLine;
    private int _eboStrip;
    private int _eboCap;
    private int _vaoNative;
    private int _vaoLine;
    private int _vaoStrip;
    private int _vaoCap;

    private ProgramInfo _nativeProgram = null!;
    private ProgramInfo _lineProgram = null!;
    private ProgramInfo _stripQuadProgram = null!;
    private ProgramInfo _stripGapProgram = null!;
    private ProgramInfo _stripCapProgram = null!;

    private int _transformFeedback;
    private int _transformFeedbackQuery;

    private float _maxLineWidth = 1.0f;       // GL_ALIASED_LINE_WIDTH_RANGE max
    private float _maxSmoothLineWidth = 1.0f; // aliased max clamped to GL_SMOOTH_LINE_WIDTH_RANGE max

    // Scratch index buffers, reused every call to keep the hot path allocation-free.
    private uint[] _lineIndices = Array.Empty<uint>();
    private uint[] _stripIndices = Array.Empty<uint>();
    private uint[] _capIndices = Array.Empty<uint>();

    private int _cachedLineCount = -1;
    private int _cachedStripCount = -1;
    private bool _cachedStripClosed;
    private int _cachedCapCount = -1;

    private ThickLineRenderer() { }

    public void Draw(ThickLineMode mode, List<float> vertices, float width, bool smooth, ref Matrix4 projection)
    {
        int count = vertices.Count / SrcFloats;
        if (count < 2) return;
        if (mode == ThickLineMode.LineLoop && count < 3) return;
        if (width < 1.0f) width = 1.0f;

        EnsureInitialized();

        // Never trust the reported line width range for widths > 1: some drivers
        // (e.g. Mesa on Intel) report a max of 7.375 but render every GL line at 1px.
        // Only the spec-guaranteed 1px goes down the native path.
        if (width <= 1.0f)
        {
            GL.UseProgram(_nativeProgram.Handle);
            GL.UniformMatrix4(_nativeProgram.Projection, false, ref projection);
            GL.Uniform1(_nativeProgram.Alpha, 1.0f);

            DrawNative(mode, vertices, count, width, smooth);
            return;
        }

        float half = width * 0.5f;
        float outer = smooth ? half + 0.5f : half;
        float feather = smooth ? 1.0f : 0.0f;
        int fanSegments = FanSegments(outer);

        var span = CollectionsMarshal.AsSpan(vertices);
        GL.BindVertexArray(_vaoNative);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, span.Length * sizeof(float), ref span[0], BufferUsageHint.DynamicDraw);

        // The feathered edge only works when blending is on; trail renderers disable it
        // when they finish, so make sure it is on for this draw and restore it after.
        bool blendWasEnabled = GL.IsEnabled(EnableCap.Blend);
        GL.Enable(EnableCap.Blend);

        switch (mode)
        {
            case ThickLineMode.Lines:
                DrawLines(count, outer, feather, fanSegments, ref projection);
                break;
            case ThickLineMode.LineStrip:
                DrawStrip(count, false, outer, feather, fanSegments, ref projection);
                break;
            case ThickLineMode.LineLoop:
                DrawStrip(count, true, outer, feather, fanSegments, ref projection);
                break;
        }

        if (!blendWasEnabled) GL.Disable(EnableCap.Blend);
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

    private void DrawLines(int count, float outer, float feather, int fanSegments, ref Matrix4 projection)
    {
        EnsureIndexCapacity(ref _lineIndices, count * 2);
        int lineIndexCount = BuildLineIndices(_lineIndices, count);

        GL.BindVertexArray(_vaoLine);
        if (_cachedLineCount != count)
        {
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _eboLine);
            GL.BufferData(
                BufferTarget.ElementArrayBuffer,
                lineIndexCount * sizeof(uint),
                ref _lineIndices[0],
                BufferUsageHint.DynamicDraw);
            _cachedLineCount = count;
        }

        BeginProgram(_lineProgram, outer, feather, 1.0f, ref projection);
        GL.Uniform1(_lineProgram.FanSegments, fanSegments);
        GL.DrawElements(PrimitiveType.Lines, lineIndexCount, DrawElementsType.UnsignedInt, 0);
    }

    private void DrawStrip(int count, bool closed, float outer, float feather, int fanSegments, ref Matrix4 projection)
    {
        EnsureIndexCapacity(ref _stripIndices, count + 3);
        int stripIndexCount = BuildStripIndices(_stripIndices, count, closed);

        GL.BindVertexArray(_vaoStrip);
        if (_cachedStripCount != count || _cachedStripClosed != closed)
        {
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _eboStrip);
            GL.BufferData(
                BufferTarget.ElementArrayBuffer,
                stripIndexCount * sizeof(uint),
                ref _stripIndices[0],
                BufferUsageHint.DynamicDraw);
            _cachedStripCount = count;
            _cachedStripClosed = closed;
        }

        BeginProgram(_stripQuadProgram, outer, feather, 1.0f, ref projection);
        GL.Uniform1(_stripQuadProgram.Closed, closed ? 1 : 0);
        GL.Uniform1(_stripQuadProgram.Count, count);
        GL.DrawElements(PrimitiveType.LineStripAdjacency, stripIndexCount, DrawElementsType.UnsignedInt, 0);

        BeginProgram(_stripGapProgram, outer, feather, 1.0f, ref projection);
        GL.Uniform1(_stripGapProgram.Closed, closed ? 1 : 0);
        GL.DrawElements(PrimitiveType.LineStripAdjacency, stripIndexCount, DrawElementsType.UnsignedInt, 0);

        if (feather <= 0.0f || closed) return;

        EnsureIndexCapacity(ref _capIndices, 4);
        _capIndices[0] = 1;
        _capIndices[1] = 0;
        _capIndices[2] = (uint)(count - 2);
        _capIndices[3] = (uint)(count - 1);

        GL.BindVertexArray(_vaoCap);
        if (_cachedCapCount != count)
        {
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _eboCap);
            GL.BufferData(
                BufferTarget.ElementArrayBuffer,
                4 * sizeof(uint),
                ref _capIndices[0],
                BufferUsageHint.DynamicDraw);
            _cachedCapCount = count;
        }

        BeginProgram(_stripCapProgram, outer, feather, 1.0f, ref projection);
        GL.Uniform1(_stripCapProgram.FanSegments, fanSegments);
        GL.DrawElements(PrimitiveType.Lines, 4, DrawElementsType.UnsignedInt, 0);
    }

    public sealed class Geometry : IDisposable
    {
        internal readonly int[] CachedVaos = new int[3];
        internal readonly int[] CachedVbos = new int[3];
        internal readonly int[] CachedVertexCounts = new int[3];
        internal int CachedPartCount;
        internal int VertexCount;
        internal float Outer;
        internal float Feather;

        public void Dispose()
        {
            for (int i = 0; i < CachedVaos.Length; i++)
            {
                if (CachedVaos[i] != 0) GL.DeleteVertexArray(CachedVaos[i]);
                if (CachedVbos[i] != 0) GL.DeleteBuffer(CachedVbos[i]);
                CachedVaos[i] = 0;
                CachedVbos[i] = 0;
                CachedVertexCounts[i] = 0;
            }

            CachedPartCount = 0;
            VertexCount = 0;
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
        int fanSegments = FanSegments(geometry.Outer);
        geometry.VertexCount = count;

        int sourceVbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, sourceVbo);
        var span = CollectionsMarshal.AsSpan(vertices);
        GL.BufferData(
            BufferTarget.ArrayBuffer,
            count * SrcFloats * sizeof(float),
            ref span[0],
            BufferUsageHint.StaticDraw);

        if (mode == ThickLineMode.Lines)
        {
            EnsureIndexCapacity(ref _lineIndices, count * 2);
            int lineIndexCount = BuildLineIndices(_lineIndices, count);

            int sourceEbo = GL.GenBuffer();
            int sourceVao = CreateSourceVertexArray(sourceVbo, sourceEbo);
            GL.BufferData(
                BufferTarget.ElementArrayBuffer,
                lineIndexCount * sizeof(uint),
                ref _lineIndices[0],
                BufferUsageHint.StaticDraw);

            int segments = count / 2;
            int maxVertices = segments * (6 + (smooth ? 6 * fanSegments : 0));
            CaptureExpandedGeometry(
                geometry,
                0,
                sourceVao,
                _lineProgram,
                PrimitiveType.Lines,
                lineIndexCount,
                maxVertices,
                () => GL.Uniform1(_lineProgram.FanSegments, fanSegments));
            geometry.CachedPartCount = 1;

            GL.DeleteVertexArray(sourceVao);
            GL.DeleteBuffer(sourceEbo);
            GL.DeleteBuffer(sourceVbo);
            return geometry;
        }

        bool closed = mode == ThickLineMode.LineLoop;

        EnsureIndexCapacity(ref _stripIndices, count + 3);
        int stripIndexCount = BuildStripIndices(_stripIndices, count, closed);

        int stripEbo = GL.GenBuffer();
        int stripVao = CreateSourceVertexArray(sourceVbo, stripEbo);
        GL.BufferData(
            BufferTarget.ElementArrayBuffer,
            stripIndexCount * sizeof(uint),
            ref _stripIndices[0],
            BufferUsageHint.StaticDraw);

        int segmentCount = closed ? count : count - 1;
        CaptureExpandedGeometry(
            geometry,
            0,
            stripVao,
            _stripQuadProgram,
            PrimitiveType.LineStripAdjacency,
            stripIndexCount,
            segmentCount * 6,
            () =>
            {
                GL.Uniform1(_stripQuadProgram.Closed, closed ? 1 : 0);
                GL.Uniform1(_stripQuadProgram.Count, count);
            });
        CaptureExpandedGeometry(
            geometry,
            1,
            stripVao,
            _stripGapProgram,
            PrimitiveType.LineStripAdjacency,
            stripIndexCount,
            count * 3,
            () =>
            {
                GL.Uniform1(_stripGapProgram.Closed, closed ? 1 : 0);
                GL.Uniform1(_stripGapProgram.Count, count);
            });
        geometry.CachedPartCount = 2;

        if (smooth && !closed)
        {
            EnsureIndexCapacity(ref _capIndices, 4);
            _capIndices[0] = 1;
            _capIndices[1] = 0;
            _capIndices[2] = (uint)(count - 2);
            _capIndices[3] = (uint)(count - 1);

            int capEbo = GL.GenBuffer();
            int capVao = CreateSourceVertexArray(sourceVbo, capEbo);
            GL.BufferData(
                BufferTarget.ElementArrayBuffer,
                4 * sizeof(uint),
                ref _capIndices[0],
                BufferUsageHint.StaticDraw);

            CaptureExpandedGeometry(
                geometry,
                2,
                capVao,
                _stripCapProgram,
                PrimitiveType.Lines,
                4,
                6 * fanSegments,
                () => GL.Uniform1(_stripCapProgram.FanSegments, fanSegments));
            geometry.CachedPartCount = 3;

            GL.DeleteVertexArray(capVao);
            GL.DeleteBuffer(capEbo);
        }

        GL.DeleteVertexArray(stripVao);
        GL.DeleteBuffer(stripEbo);
        GL.DeleteBuffer(sourceVbo);
        return geometry;
    }

    public void DrawGeometry(Geometry geometry, float alpha, ref Matrix4 projection)
    {
        if (geometry == null || geometry.VertexCount == 0) return;

        EnsureInitialized();

        BeginProgram(_nativeProgram, geometry.Outer, geometry.Feather, alpha, ref projection);
        for (int i = 0; i < geometry.CachedPartCount; i++)
        {
            int vertexCount = geometry.CachedVertexCounts[i];
            if (vertexCount == 0) continue;

            GL.BindVertexArray(geometry.CachedVaos[i]);
            GL.DrawArrays(PrimitiveType.Triangles, 0, vertexCount);
        }
    }

    private void CaptureExpandedGeometry(
        Geometry geometry,
        int part,
        int sourceVao,
        ProgramInfo program,
        PrimitiveType primitive,
        int indexCount,
        int maxVertices,
        Action setUniforms)
    {
        int cachedVbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, cachedVbo);
        GL.BufferData(
            BufferTarget.ArrayBuffer,
            maxVertices * ExpandedFloats * sizeof(float),
            IntPtr.Zero,
            BufferUsageHint.StaticCopy);

        GL.BindTransformFeedback(TransformFeedbackTarget.TransformFeedback, _transformFeedback);
        GL.BindBufferBase(BufferRangeTarget.TransformFeedbackBuffer, 0, cachedVbo);

        GL.Enable(EnableCap.RasterizerDiscard);
        GL.BeginQuery(QueryTarget.TransformFeedbackPrimitivesWritten, _transformFeedbackQuery);

        var identity = Matrix4.Identity;
        BeginProgram(program, geometry.Outer, geometry.Feather, 1.0f, ref identity);
        setUniforms();

        GL.BeginTransformFeedback(TransformFeedbackPrimitiveType.Triangles);
        GL.BindVertexArray(sourceVao);
        GL.DrawElements(primitive, indexCount, DrawElementsType.UnsignedInt, 0);
        GL.EndTransformFeedback();
        GL.EndQuery(QueryTarget.TransformFeedbackPrimitivesWritten);
        GL.Disable(EnableCap.RasterizerDiscard);

        GL.GetQueryObject(_transformFeedbackQuery, GetQueryObjectParam.QueryResult, out int primitivesWritten);

        geometry.CachedVbos[part] = cachedVbo;
        geometry.CachedVaos[part] = CreateExpandedVertexArray(cachedVbo);
        geometry.CachedVertexCounts[part] = primitivesWritten * 3;
    }

    private static void BeginProgram(ProgramInfo program, float outer, float feather, float alpha, ref Matrix4 projection)
    {
        GL.UseProgram(program.Handle);
        GL.UniformMatrix4(program.Projection, false, ref projection);
        GL.Uniform1(program.Outer, outer);
        GL.Uniform1(program.Feather, feather);
        GL.Uniform1(program.Alpha, alpha);
    }

    private static int BuildLineIndices(uint[] indices, int count)
    {
        int p = 0;
        for (int i = 0; i + 1 < count; i += 2)
        {
            uint a = (uint)i;
            uint b = (uint)(i + 1);
            indices[p++] = a;
            indices[p++] = b;
            indices[p++] = a;
            indices[p++] = b;
        }
        return p;
    }

    private static int BuildStripIndices(uint[] indices, int count, bool closed)
    {
        int p = 0;
        if (closed)
        {
            indices[p++] = (uint)(count - 1);
            for (int i = 0; i < count; i++)
                indices[p++] = (uint)i;
            indices[p++] = 0;
            indices[p++] = 1;
        }
        else
        {
            indices[p++] = 0;
            indices[p++] = 0;
            for (int i = 1; i < count; i++)
                indices[p++] = (uint)i;
            indices[p++] = (uint)(count - 1);
        }
        return p;
    }

    private static void EnsureIndexCapacity(ref uint[] indices, int required)
    {
        if (indices.Length >= required) return;
        Array.Resize(ref indices, Math.Max(required, Math.Max(16, indices.Length * 2)));
    }

    private static int FanSegments(float outer)
    {
        float step = 2.0f * MathF.Asin(Math.Clamp(0.5f / outer, 0.05f, 1.0f));
        return Math.Clamp((int)MathF.Ceiling(MathF.PI / step), 4, 32);
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
        _eboLine = GL.GenBuffer();
        _eboStrip = GL.GenBuffer();
        _eboCap = GL.GenBuffer();

        _vaoNative = CreateSourceVertexArray(_vbo, 0);
        _vaoLine = CreateSourceVertexArray(_vbo, _eboLine);
        _vaoStrip = CreateSourceVertexArray(_vbo, _eboStrip);
        _vaoCap = CreateSourceVertexArray(_vbo, _eboCap);

        _transformFeedback = GL.GenTransformFeedback();
        _transformFeedbackQuery = GL.GenQuery();

        CreateShaderPrograms();

        GL.BindVertexArray(prevVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, prevBuffer);

        Console.WriteLine(
            $"ThickLineRenderer: driver reports aliased line width up to {_maxLineWidth}px, " +
            $"smooth up to {_maxSmoothLineWidth}px (only 1px is trusted; wider lines are expanded)");

        _initialized = true;
    }

    private static int CreateExpandedVertexArray(int vbo)
    {
        int vao = GL.GenVertexArray();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);

        int stride = ExpandedFloats * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, 4 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
        GL.EnableVertexAttribArray(2);

        return vao;
    }

    private static int CreateSourceVertexArray(int vbo, int ebo)
    {
        int vao = GL.GenVertexArray();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);

        int stride = SrcFloats * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);

        if (ebo != 0) GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);

        return vao;
    }

    private void CreateShaderPrograms()
    {
        _nativeProgram = CreateProgram(NativeVertexShaderSource, null, FragmentShaderSource, false);
        _lineProgram = CreateProgram(GeometryVertexShaderSource, LineGeometryShaderSource, FragmentShaderSource, true);
        _stripQuadProgram = CreateProgram(GeometryVertexShaderSource, StripQuadGeometryShaderSource, FragmentShaderSource, true);
        _stripGapProgram = CreateProgram(GeometryVertexShaderSource, StripGapGeometryShaderSource, FragmentShaderSource, true);
        _stripCapProgram = CreateProgram(GeometryVertexShaderSource, StripCapGeometryShaderSource, FragmentShaderSource, true);
    }

    private static ProgramInfo CreateProgram(string vertexSource, string? geometrySource, string fragmentSource, bool transformFeedback)
    {
        int vertexShader = CompileShader(ShaderType.VertexShader, vertexSource);
        int geometryShader = geometrySource == null ? 0 : CompileShader(ShaderType.GeometryShader, geometrySource);
        int fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource);

        int program = GL.CreateProgram();
        GL.AttachShader(program, vertexShader);
        if (geometryShader != 0) GL.AttachShader(program, geometryShader);
        GL.AttachShader(program, fragmentShader);
        if (transformFeedback)
        {
            GL.TransformFeedbackVaryings(
                program,
                3,
                new[] { "gl_Position", "vertexColor", "side" },
                TransformFeedbackMode.InterleavedAttribs);
        }
        GL.LinkProgram(program);

        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
            Console.WriteLine($"ThickLineRenderer link error: {GL.GetProgramInfoLog(program)}");

        GL.DeleteShader(vertexShader);
        if (geometryShader != 0) GL.DeleteShader(geometryShader);
        GL.DeleteShader(fragmentShader);

        return new ProgramInfo
        {
            Handle = program,
            Projection = GL.GetUniformLocation(program, "projection"),
            Outer = GL.GetUniformLocation(program, "uOuter"),
            Feather = GL.GetUniformLocation(program, "uFeather"),
            Alpha = GL.GetUniformLocation(program, "uAlpha"),
            FanSegments = GL.GetUniformLocation(program, "uFanSegments"),
            Closed = GL.GetUniformLocation(program, "uClosed"),
            Count = GL.GetUniformLocation(program, "uCount")
        };
    }

    private static int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);

        GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);
        if (status == 0)
            Console.WriteLine($"ThickLineRenderer {type} compile error: {GL.GetShaderInfoLog(shader)}");

        return shader;
    }

    private sealed class ProgramInfo
    {
        public int Handle;
        public int Projection;
        public int Outer;
        public int Feather;
        public int Alpha;
        public int FanSegments;
        public int Closed;
        public int Count;
    }

    private const string NativeVertexShaderSource = @"#version 330 core
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

    private const string GeometryVertexShaderSource = @"#version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec4 aColor;

        out vec4 vColor;

        void main()
        {
            gl_Position = vec4(aPosition, 1.0);
            vColor = aColor;
        }";

    private const string FragmentShaderSource = @"#version 330 core
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

    private const string GeometryCommonSource = @"
        in vec4 vColor[];

        out vec4 vertexColor;
        out float side;

        uniform mat4 projection;
        uniform float uOuter;
        uniform int uFanSegments;
        uniform int uClosed;
        uniform int uCount;

        const float Pi = 3.141592653589793;
        const float MiterLimit = 4.0;

        vec2 normalizeOr(vec2 v, vec2 fallback)
        {
            float lenSq = dot(v, v);
            return lenSq > 1e-12 ? v / sqrt(lenSq) : fallback;
        }

        vec2 normalizeDir(vec2 v)
        {
            return normalizeOr(v, vec2(1.0, 0.0));
        }

        vec2 perp(vec2 v)
        {
            return vec2(v.y, -v.x);
        }

        void computeOffsets(vec2 pPrev, vec2 pCur, vec2 pNext, bool hasIn, bool hasOut, out vec2 inM, out vec2 outM)
        {
            inM = vec2(0.0);
            outM = vec2(0.0);
            bool mitre = false;

            if (hasIn && hasOut)
            {
                vec2 n1 = perp(normalizeDir(pCur - pPrev));
                vec2 n2 = perp(normalizeDir(pNext - pCur));
                vec2 bis = n1 + n2;
                float bisLenSq = dot(bis, bis);

                if (bisLenSq >= 1e-8)
                {
                    bis /= sqrt(bisLenSq);
                    float cosHalf = dot(bis, n2);
                    if (cosHalf >= 1.0 / MiterLimit)
                    {
                        inM = outM = bis * (uOuter / cosHalf);
                        mitre = true;
                    }
                }
            }

            if (!mitre)
            {
                if (hasIn) inM = perp(normalizeDir(pCur - pPrev)) * uOuter;
                if (hasOut) outM = perp(normalizeDir(pNext - pCur)) * uOuter;
            }
        }

        void emitVertex(vec3 position, vec4 color, float vertexSide)
        {
            gl_Position = projection * vec4(position, 1.0);
            vertexColor = color;
            side = vertexSide;
            EmitVertex();
        }

        void emitCap(vec3 center, vec4 color, vec2 outDir, int segments)
        {
            float baseAngle = atan(outDir.y, outDir.x) - Pi * 0.5;
            vec2 prev = center.xy + vec2(cos(baseAngle), sin(baseAngle)) * uOuter;

            for (int j = 1; j <= segments; j++)
            {
                float angle = baseAngle + Pi * float(j) / float(segments);
                vec2 next = center.xy + vec2(cos(angle), sin(angle)) * uOuter;

                emitVertex(center, color, 0.0);
                emitVertex(vec3(prev, center.z), color, uOuter);
                emitVertex(vec3(next, center.z), color, uOuter);
                EndPrimitive();

                prev = next;
            }
        }";

    private const string LineGeometryShaderSource = @"#version 330 core
        layout(lines) in;
        layout(triangle_strip, max_vertices = 102) out;
        " + GeometryCommonSource + @"
        uniform float uFeather;

        void main()
        {
            vec3 a = gl_in[0].gl_Position.xyz;
            vec3 b = gl_in[1].gl_Position.xyz;
            vec2 d = b.xy - a.xy;
            float lenSq = dot(d, d);

            bool endCapPass = (gl_PrimitiveIDIn & 1) != 0;

            if (lenSq < 1e-12)
            {
                if (uFeather <= 0.0) return;

                if (endCapPass)
                    emitCap(a, vColor[0], vec2(-1.0, 0.0), uFanSegments);
                else
                    emitCap(a, vColor[0], vec2(1.0, 0.0), uFanSegments);
                return;
            }

            if (endCapPass)
            {
                if (uFeather > 0.0)
                {
                    vec2 dir = d / sqrt(lenSq);
                    emitCap(b, vColor[1], dir, uFanSegments);
                }
                return;
            }

            vec2 dir = d / sqrt(lenSq);
            vec2 n = vec2(dir.y, -dir.x) * uOuter;
            vec4 ca = vColor[0];
            vec4 cb = vColor[1];

            emitVertex(vec3(a.xy + n, a.z), ca, uOuter);
            emitVertex(vec3(b.xy + n, b.z), cb, uOuter);
            emitVertex(vec3(b.xy - n, b.z), cb, -uOuter);
            EndPrimitive();

            emitVertex(vec3(a.xy + n, a.z), ca, uOuter);
            emitVertex(vec3(b.xy - n, b.z), cb, -uOuter);
            emitVertex(vec3(a.xy - n, a.z), ca, -uOuter);
            EndPrimitive();

            if (uFeather > 0.0)
                emitCap(a, ca, -dir, uFanSegments);
        }";

    private const string StripQuadGeometryShaderSource = @"#version 330 core
        layout(lines_adjacency) in;
        layout(triangle_strip, max_vertices = 6) out;
        " + GeometryCommonSource + @"

        void main()
        {
            vec3 pPrev = gl_in[0].gl_Position.xyz;
            vec3 pCur = gl_in[1].gl_Position.xyz;
            vec3 pNext = gl_in[2].gl_Position.xyz;
            vec3 pAfter = gl_in[3].gl_Position.xyz;

            int i = gl_PrimitiveIDIn;
            bool hasIn = (uClosed != 0) || (i > 0);
            bool hasOut = (uClosed != 0) || (i + 1 < uCount - 1);

            vec2 inM1;
            vec2 outM1;
            vec2 inM2;
            vec2 outM2;

            computeOffsets(pPrev.xy, pCur.xy, pNext.xy, hasIn, true, inM1, outM1);
            computeOffsets(pCur.xy, pNext.xy, pAfter.xy, true, hasOut, inM2, outM2);

            vec2 si = outM1;
            vec2 sj = inM2;

            vec2 li = pCur.xy + si;
            vec2 lj = pNext.xy + sj;
            vec2 ri = pCur.xy - si;
            vec2 rj = pNext.xy - sj;

            vec4 ci = vColor[1];
            vec4 cj = vColor[2];

            emitVertex(vec3(li, pCur.z), ci, uOuter);
            emitVertex(vec3(lj, pNext.z), cj, uOuter);
            emitVertex(vec3(rj, pNext.z), cj, -uOuter);
            EndPrimitive();

            emitVertex(vec3(li, pCur.z), ci, uOuter);
            emitVertex(vec3(rj, pNext.z), cj, -uOuter);
            emitVertex(vec3(ri, pCur.z), ci, -uOuter);
            EndPrimitive();
        }";

    private const string StripGapGeometryShaderSource = @"#version 330 core
        layout(lines_adjacency) in;
        layout(triangle_strip, max_vertices = 3) out;
        " + GeometryCommonSource + @"

        void main()
        {
            if (uClosed == 0 && gl_PrimitiveIDIn == 0) return;

            vec3 pPrev = gl_in[0].gl_Position.xyz;
            vec3 pCur = gl_in[1].gl_Position.xyz;
            vec3 pNext = gl_in[2].gl_Position.xyz;

            vec2 inM;
            vec2 outM;
            computeOffsets(pPrev.xy, pCur.xy, pNext.xy, true, true, inM, outM);

            if (abs(inM.x - outM.x) < 1e-5 && abs(inM.y - outM.y) < 1e-5) return;

            vec4 color = vColor[1];

            emitVertex(vec3(pCur.xy + inM, pCur.z), color, uOuter);
            emitVertex(pCur, color, 0.0);
            emitVertex(vec3(pCur.xy + outM, pCur.z), color, uOuter);
            EndPrimitive();
        }";

    private const string StripCapGeometryShaderSource = @"#version 330 core
        layout(lines) in;
        layout(triangle_strip, max_vertices = 96) out;
        " + GeometryCommonSource + @"

        void main()
        {
            vec3 p0 = gl_in[0].gl_Position.xyz;
            vec3 p1 = gl_in[1].gl_Position.xyz;
            vec2 fallback = (gl_PrimitiveIDIn == 0) ? vec2(-1.0, 0.0) : vec2(1.0, 0.0);
            vec2 dir = normalizeOr(p1.xy - p0.xy, fallback);

            emitCap(p1, vColor[1], dir, uFanSegments);
        }";
}
