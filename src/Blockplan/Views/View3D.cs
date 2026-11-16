using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Blockplan.Core;
using Silk.NET.OpenGL;

namespace Blockplan.Views;

/// <summary>3D-Ansicht mit OpenGL. Linke Maustaste dreht, rechte verschiebt, Mausrad zoomt.</summary>
public sealed class View3D : OpenGlControlBase, Avalonia.Rendering.ICustomHitTest
{
    GL? gl; uint prog, atlasTex, avgTex; int uAtlas, uAvg; int uMvp, uCols, uFogNear, uFogFar, uCam, uSky;
    readonly List<(uint vao, uint vbo, uint ebo, int count)> meshes = new();
    List<MeshChunk>? pending; (byte[] px, int cols)? pendingAtlas; bool clearMeshes;
    int atlasCols = 1;
    Project? project; Atlas? atlas; int top;
    CancellationTokenSource? cts;

    // Kamera
    float yaw = -0.75f, pitch = 0.62f, dist = 80; Vector3 target;
    Point? dragFrom; bool panning;

    public event Action<string>? Status;
    public event Action<string>? Hover;

    static readonly Vector3 Sky = new(0.47f, 0.65f, 1.0f);

    public View3D()
    {
        Focusable = true; ClipToBounds = true;
        // Umschalt/Strg für schnelleres/langsameres Fliegen merken
        AddHandler(KeyDownEvent, (_, e) => lastMods = e.KeyModifiers, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        AddHandler(KeyUpEvent, (_, e) => lastMods = e.KeyModifiers, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
    }

    /// <summary>Die OpenGL-Fläche zeichnet selbst; ohne das bekäme sie keine Maus-Ereignisse.</summary>
    public bool HitTest(Point point) => true;

    /* ---------- Daten ---------- */
    public void SetProject(Project? p, Atlas a, int topY, bool refit)
    {
        project = p; atlas = a; top = topY;
        if (p != null && refit) Fit();
        Rebuild();
    }

    public void SetTop(int topY) { if (topY == top) return; top = topY; Rebuild(); }

    void Fit()
    {
        if (project == null) return;
        int my = Math.Max(1, project.MaxY());
        target = new Vector3(0, Math.Min(project.H, my + 1) * 0.4f, 0);
        dist = Math.Max(Math.Max(project.W, project.D), 8) * 1.3f; yaw = -0.75f; pitch = 0.62f;
        if (float.TryParse(Environment.GetEnvironmentVariable("BLOCKPLAN_DIST"), System.Globalization.CultureInfo.InvariantCulture, out var dd)) { dist = dd; target.Z = project.D * 0.12f; }
    }

    public void ResetView() { Fit(); RequestNextFrameRendering(); }

    void Rebuild()
    {
        cts?.Cancel(); var my = new CancellationTokenSource(); cts = my;
        var p = project; var a = atlas; int t = top;
        if (p == null || a == null) { lock (this) { pending = new(); clearMeshes = true; } RequestNextFrameRendering(); return; }
        Status?.Invoke("Baue 3D-Ansicht …");
        Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var m = Mesher.Build(p, t, a, my.Token);
            if (my.IsCancellationRequested) return;
            var at = a.Build();
            long tris = 0; foreach (var c in m) tris += c.I.Length / 3;
            lock (this) { pending = m; pendingAtlas = at; clearMeshes = true; }
            Dispatcher.UIThread.Post(() => { if (cts == my) { Status?.Invoke($"3D: {tris:N0} Dreiecke in {sw.ElapsedMilliseconds:N0} ms"); RequestNextFrameRendering(); } });
        }, my.Token);
    }

    /* ---------- OpenGL ---------- */
    protected override void OnOpenGlInit(GlInterface gi)
    {
        Console.Error.WriteLine($"[3D] OpenGL {GlVersion.Type} {GlVersion.Major}.{GlVersion.Minor}");
        gl = GL.GetApi(gi.GetProcAddress);
        bool es = GlVersion.Type == GlProfileType.OpenGLES;
        string head = es ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";
        string vs = head + @"
layout(location=0) in vec3 pos; layout(location=1) in vec2 tuv; layout(location=2) in float td;
uniform mat4 mvp; uniform vec3 cam;
out vec2 vUv; out float vTd; out float vDist;
void main(){ vUv=tuv; vTd=td; vDist=distance(pos,cam); gl_Position=mvp*vec4(pos,1.0); }";
        string fs = head + @"
uniform sampler2D atlas; uniform sampler2D avgTex; uniform float cols; uniform float fogNear; uniform float fogFar; uniform vec3 sky;
in vec2 vUv; in float vTd; in float vDist; out vec4 outColor;
void main(){
  float t=floor((vTd+0.5)/8.0); float d=floor(vTd+0.5)-t*8.0;
  float sh = d<1.5?0.6:(d<2.5?1.0:(d<3.5?0.5:(d<5.5?0.8:0.9)));
  vec2 f=clamp(fract(vUv),0.002,0.998); vec2 o=vec2(mod(t,cols),floor(t/cols));
  vec4 c=texture(atlas,(o+vec2(f.x,1.0-f.y))/cols); if(c.a<0.5) discard;
  // weit weg: Durchschnittsfarbe der Kachel statt Pixelrauschen
  float tpp=16.0*max(length(dFdx(vUv)),length(dFdy(vUv)));
  vec3 avg=texelFetch(avgTex,ivec2(int(o.x),int(o.y)),0).rgb;
  c.rgb=mix(c.rgb,avg,clamp((tpp-0.8)/2.5,0.0,1.0));
  float fog=clamp((vDist-fogNear)/(fogFar-fogNear),0.0,1.0);
  outColor=vec4(mix(c.rgb*sh,sky,fog),1.0);
}";
        try { prog = Link(vs, fs); } catch (Exception ex) { Console.Error.WriteLine("[3D] " + ex.Message); throw; }
        uMvp = gl.GetUniformLocation(prog, "mvp"); uCols = gl.GetUniformLocation(prog, "cols"); uCam = gl.GetUniformLocation(prog, "cam");
        uFogNear = gl.GetUniformLocation(prog, "fogNear"); uFogFar = gl.GetUniformLocation(prog, "fogFar"); uSky = gl.GetUniformLocation(prog, "sky");
        atlasTex = gl.GenTexture(); avgTex = gl.GenTexture();
        uAtlas = gl.GetUniformLocation(prog, "atlas"); uAvg = gl.GetUniformLocation(prog, "avgTex");
    }

    uint Link(string vs, string fs)
    {
        uint Sh(ShaderType t, string src)
        {
            var s = gl!.CreateShader(t); gl.ShaderSource(s, src); gl.CompileShader(s);
            gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new Exception("Shader: " + gl.GetShaderInfoLog(s));
            return s;
        }
        uint p = gl!.CreateProgram(), a = Sh(ShaderType.VertexShader, vs), b = Sh(ShaderType.FragmentShader, fs);
        gl.AttachShader(p, a); gl.AttachShader(p, b); gl.LinkProgram(p);
        gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0) throw new Exception("Programm: " + gl.GetProgramInfoLog(p));
        gl.DeleteShader(a); gl.DeleteShader(b); return p;
    }

    protected override void OnOpenGlDeinit(GlInterface gi)
    {
        if (gl == null) return;
        foreach (var m in meshes) { gl.DeleteVertexArray(m.vao); gl.DeleteBuffer(m.vbo); gl.DeleteBuffer(m.ebo); }
        meshes.Clear(); gl.DeleteProgram(prog); gl.DeleteTexture(atlasTex); gl.DeleteTexture(avgTex); gl = null;
    }

    unsafe void Upload()
    {
        List<MeshChunk>? m; (byte[] px, int cols)? at; bool clr;
        lock (this) { m = pending; at = pendingAtlas; clr = clearMeshes; pending = null; pendingAtlas = null; clearMeshes = false; }
        if (clr) { foreach (var x in meshes) { gl!.DeleteVertexArray(x.vao); gl.DeleteBuffer(x.vbo); gl.DeleteBuffer(x.ebo); } meshes.Clear(); }
        if (at is { } a)
        {
            int S = a.cols * Atlas.T; atlasCols = a.cols;
            gl!.BindTexture(TextureTarget.Texture2D, atlasTex);
            fixed (byte* p = a.px) gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, (uint)S, (uint)S, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            // Durchschnittsfarbe je Kachel (1 Texel je Kachel)
            var avg = new byte[a.cols * a.cols * 4];
            for (int ty = 0; ty < a.cols; ty++) for (int tx = 0; tx < a.cols; tx++)
            {
                long r = 0, g = 0, b = 0, n = 0;
                for (int y = 0; y < Atlas.T; y++) for (int x = 0; x < Atlas.T; x++) { int o = (((ty * Atlas.T + y) * S) + tx * Atlas.T + x) * 4; if (a.px[o + 3] > 100) { r += a.px[o]; g += a.px[o + 1]; b += a.px[o + 2]; n++; } }
                int d = (ty * a.cols + tx) * 4; if (n > 0) { avg[d] = (byte)(r / n); avg[d + 1] = (byte)(g / n); avg[d + 2] = (byte)(b / n); } avg[d + 3] = 255;
            }
            gl.BindTexture(TextureTarget.Texture2D, avgTex);
            fixed (byte* p = avg) gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, (uint)a.cols, (uint)a.cols, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        }
        if (m == null) return;
        foreach (var c in m)
        {
            uint vao = gl!.GenVertexArray(); gl.BindVertexArray(vao);
            uint vbo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            fixed (float* p = c.V) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(c.V.Length * 4), p, BufferUsageARB.StaticDraw);
            uint ebo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
            fixed (uint* p = c.I) gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(c.I.Length * 4), p, BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 24, (void*)0);
            gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 24, (void*)12);
            gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, 24, (void*)20);
            meshes.Add((vao, vbo, ebo, c.I.Length));
        }
        gl!.BindVertexArray(0);
    }

    Vector3 CamPos() => target + new Vector3(MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Sin(yaw) * MathF.Cos(pitch)) * dist;

    /* ---------- Flugmodus: Kamera frei im Raum, wie in Minecraft im Zuschauermodus ---------- */
    public bool Fly { get; private set; }
    public float Speed { get; set; } = 8;              // Blöcke pro Sekunde im Flugmodus
    public event Action<bool>? FlyChanged;
    Vector3 eye; float lookYaw, lookPitch;
    static Vector3 Dir(float y, float p) => new(MathF.Cos(y) * MathF.Cos(p), MathF.Sin(p), MathF.Sin(y) * MathF.Cos(p));
    Vector3 Eye() => Fly ? eye : CamPos();
    Vector3 Forward() => Fly ? Dir(lookYaw, lookPitch) : Vector3.Normalize(target - CamPos());

    public void SetFly(bool on)
    {
        if (on == Fly) return;
        if (on)
        {
            eye = CamPos(); var f = Vector3.Normalize(target - eye);
            lookYaw = MathF.Atan2(f.Z, f.X); lookPitch = MathF.Asin(Math.Clamp(f.Y, -1, 1));
        }
        else
        {
            var f = Dir(lookYaw, lookPitch); target = eye + f * dist;
            yaw = MathF.Atan2(-f.Z, -f.X); pitch = Math.Clamp(MathF.Asin(Math.Clamp(-f.Y, -1, 1)), -1.5f, 1.5f);
        }
        Fly = on; FlyChanged?.Invoke(on); RequestNextFrameRendering();
    }

    Matrix4x4 ViewProj(float aspect)
    {
        var cam = Eye();
        var view = Matrix4x4.CreateLookAt(cam, cam + Forward(), Vector3.UnitY);
        float near = Fly ? 0.05f : Math.Max(0.05f, dist * 0.002f), far = Math.Max(5000, (Fly ? 0 : dist) * 6 + (project == null ? 0 : Math.Max(project.W, project.D) * 3));
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(70 * MathF.PI / 180, Math.Max(0.1f, aspect), near, far);
        return view * proj;
    }

    protected override unsafe void OnOpenGlRender(GlInterface gi, int fb)
    {
        if (gl == null) return;
        Upload();
        double sc = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = Math.Max(1, (int)(Bounds.Width * sc)), h = Math.Max(1, (int)(Bounds.Height * sc));
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        gl.Viewport(0, 0, (uint)w, (uint)h);
        gl.ClearColor(Sky.X, Sky.Y, Sky.Z, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        gl.Enable(EnableCap.DepthTest); gl.DepthFunc(DepthFunction.Lequal); gl.Disable(EnableCap.CullFace); gl.Disable(EnableCap.Blend);
        if (meshes.Count == 0) return;
        gl.UseProgram(prog);
        var mvp = ViewProj((float)w / h); var cam = Eye();
        gl.UniformMatrix4(uMvp, 1, false, (float*)&mvp);
        gl.Uniform1(uCols, (float)atlasCols); gl.Uniform3(uCam, cam.X, cam.Y, cam.Z); gl.Uniform3(uSky, Sky.X, Sky.Y, Sky.Z);
        float size = project == null ? 100 : Math.Max(project.W, project.D);
        float fd = Fly ? size * 0.5f : dist; gl.Uniform1(uFogNear, fd * 1.2f + size * 0.6f); gl.Uniform1(uFogFar, fd * 3f + size * 1.5f + 200);
        gl.ActiveTexture(TextureUnit.Texture0); gl.BindTexture(TextureTarget.Texture2D, atlasTex); gl.Uniform1(uAtlas, 0);
        gl.ActiveTexture(TextureUnit.Texture1); gl.BindTexture(TextureTarget.Texture2D, avgTex); gl.Uniform1(uAvg, 1); gl.ActiveTexture(TextureUnit.Texture0);
        foreach (var m in meshes) { gl.BindVertexArray(m.vao); gl.DrawElements(PrimitiveType.Triangles, (uint)m.count, DrawElementsType.UnsignedInt, (void*)0); }
        gl.BindVertexArray(0);
    }

    /* ---------- Maus ---------- */
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); Focus();
        var pp = e.GetCurrentPoint(this); dragFrom = pp.Position;
        panning = pp.Properties.IsRightButtonPressed || pp.Properties.IsMiddleButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        e.Pointer.Capture(this); e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); dragFrom = null; e.Pointer.Capture(null); }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (dragFrom is { } from)
        {
            var d = pos - from; dragFrom = pos;
            if (Fly)
            {   // umsehen
                lookYaw += (float)d.X * 0.003f; lookPitch = Math.Clamp(lookPitch - (float)d.Y * 0.003f, -1.55f, 1.55f);
            }
            else if (panning)
            {
                var fwd = Forward(); var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY)); var up = Vector3.Cross(right, fwd);
                float k = dist * 0.0018f; target += (-right * (float)d.X + up * (float)d.Y) * k;
            }
            else { yaw += (float)d.X * 0.005f; pitch = Math.Clamp(pitch + (float)d.Y * 0.005f, -1.5f, 1.5f); }
            RequestNextFrameRendering();
        }
        else Pick(pos);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Fly) eye += Forward() * (float)e.Delta.Y * Math.Max(1, Speed * 0.15f);
        else dist = Math.Clamp(dist * MathF.Exp(-(float)e.Delta.Y * 0.1f), 2, 6000);
        RequestNextFrameRendering(); e.Handled = true;
    }

    /* ---------- Tastatur: gehaltene Tasten bewegen weich, solange sie gedrückt sind ---------- */
    readonly HashSet<Key> held = new();
    DispatcherTimer? moveTimer; DateTime lastTick;
    static readonly HashSet<Key> moveKeys = new() { Key.W, Key.A, Key.S, Key.D, Key.Space, Key.C, Key.Q, Key.E, Key.Up, Key.Down, Key.Left, Key.Right };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F) { SetFly(!Fly); e.Handled = true; return; }
        if (e.Key == Key.Escape && Fly) { SetFly(false); e.Handled = true; return; }
        if (!moveKeys.Contains(e.Key)) return;
        held.Add(e.Key); e.Handled = true;
        if (moveTimer == null) { moveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => Step()); }
        if (!moveTimer.IsEnabled) { lastTick = DateTime.Now; moveTimer.Start(); Step(); }
    }

    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); held.Remove(e.Key); }
    protected override void OnLostFocus(FocusChangedEventArgs e) { base.OnLostFocus(e); held.Clear(); }

    void Step()
    {
        var now = DateTime.Now; float dt = (float)Math.Clamp((now - lastTick).TotalSeconds, 0.001, 0.1); lastTick = now;
        if (held.Count == 0) { moveTimer?.Stop(); return; }
        float sp = (Fly ? Speed : Math.Max(Speed, dist * 0.12f)) * dt;
        var mods = lastMods;
        if (mods.HasFlag(KeyModifiers.Shift)) sp *= 4; if (mods.HasFlag(KeyModifiers.Control)) sp *= 0.25f;
        var f = Forward(); var flat = new Vector3(f.X, 0, f.Z); if (flat.LengthSquared() < 1e-6f) flat = Dir(Fly ? lookYaw : yaw + MathF.PI, 0); flat = Vector3.Normalize(flat);
        var right = Vector3.Normalize(Vector3.Cross(flat, Vector3.UnitY));
        var mv = Vector3.Zero;
        if (held.Contains(Key.W) || held.Contains(Key.Up)) mv += Fly ? f : flat;
        if (held.Contains(Key.S) || held.Contains(Key.Down)) mv -= Fly ? f : flat;
        if (held.Contains(Key.D) || held.Contains(Key.Right)) mv += right;
        if (held.Contains(Key.A) || held.Contains(Key.Left)) mv -= right;
        if (held.Contains(Key.Space) || held.Contains(Key.E)) mv += Vector3.UnitY;
        if (held.Contains(Key.C) || held.Contains(Key.Q)) mv -= Vector3.UnitY;
        if (mv == Vector3.Zero) return;
        mv *= sp;
        if (Fly) eye += mv; else target += mv;
        RequestNextFrameRendering();
    }

    KeyModifiers lastMods;

    /// <summary>Block unter der Maus: Strahl durch das Raster laufen lassen.</summary>
    void Pick(Point pos)
    {
        var p = project; if (p == null || Bounds.Width < 1) return;
        float aspect = (float)(Bounds.Width / Bounds.Height);
        if (!Matrix4x4.Invert(ViewProj(aspect), out var inv)) return;
        float nx = (float)(pos.X / Bounds.Width * 2 - 1), ny = (float)(1 - pos.Y / Bounds.Height * 2);
        var a = Vector4.Transform(new Vector4(nx, ny, -1, 1), inv); var b = Vector4.Transform(new Vector4(nx, ny, 1, 1), inv);
        var o = new Vector3(a.X, a.Y, a.Z) / a.W; var e2 = new Vector3(b.X, b.Y, b.Z) / b.W; var dv = Vector3.Normalize(e2 - o);
        o += new Vector3(p.W / 2f, 0, p.D / 2f);
        int maxY = Math.Min(top, p.H - 1);
        float t = 0, step = 0.05f; int lx = int.MinValue, ly = 0, lz = 0;
        float lim = Math.Max(4000, dist * 6);
        // einfache Schrittsuche bis zum ersten Block
        while (t < lim)
        {
            var q = o + dv * t; int x = (int)MathF.Floor(q.X), y = (int)MathF.Floor(q.Y), z = (int)MathF.Floor(q.Z);
            if (x != lx || y != ly || z != lz)
            {
                lx = x; ly = y; lz = z;
                if (y >= 0 && y <= maxY && x >= 0 && z >= 0 && x < p.W && z < p.D)
                {
                    var v = p.Cells[p.Index(x, y, z)];
                    if (v != 0) { Hover?.Invoke($"{Blocks.DisplayName(v, true)}  ·  X {p.OriginX + x}  Y {p.OriginY + y}  Z {p.OriginZ + z}"); return; }
                }
                else if ((dv.Y < 0 && y < 0) || (dv.Y > 0 && y > maxY && q.Y > maxY + 1)) break;
            }
            t += step; if (t > 20) step = 0.25f;
        }
        Hover?.Invoke("");
    }
}
