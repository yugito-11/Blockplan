using System;
using System.Collections.Generic;
using System.Linq;
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
    GL? gl; uint prog, atlasTex, avgTex; int uAtlas, uAvg, uTrans; int uMvp, uCols, uFogNear, uFogFar, uCam, uSky;
    /// <summary>Geometrie in benannten Stücken: "r:<Bereich>" Projekt, "c:<cx>,<cz>" Umgebung, "players", "guides".</summary>
    readonly Dictionary<string, List<(uint vao, uint vbo, uint ebo, int count, bool trans)>> meshes = new();
    readonly List<(string key, List<MeshChunk>? chunks)> pending = new(); (byte[] px, int cols)? pendingAtlas; bool clearMeshes;
    int atlasCols = 1, atlasTiles;
    Project? project; Atlas? atlas; int top;
    CancellationTokenSource? cts;
    // Live
    LiveClient? live; bool progress, surroundings; PlanData? guidesPlan; bool showGuides = true;
    readonly object buildLock = new();

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
        // nur zum Testen: feste Kamera „yaw,pitch,dist,x,y,z“ (Grad, Blöcke; x/z ab Nordwest-Ecke)
        if (Environment.GetEnvironmentVariable("BLOCKPLAN_VIEW") is { Length: > 0 } vw)
        {
            var a = vw.Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            yaw = a[0] * MathF.PI / 180; pitch = a[1] * MathF.PI / 180; dist = a[2]; target = new Vector3(a[3] - project.W / 2f, a[4], a[5] - project.D / 2f); return;
        }
        if (float.TryParse(Environment.GetEnvironmentVariable("BLOCKPLAN_DIST"), System.Globalization.CultureInfo.InvariantCulture, out var dd)) { dist = dd; target.Z = project.D * 0.12f; }
    }

    public void ResetView() { Fit(); RequestNextFrameRendering(); }

    /// <summary>Kamera auf eine Stelle der Szene richten (z. B. einen Spieler).</summary>
    public void LookAt(double x, double y, double z)
    {
        if (Fly) SetFly(false);
        target = new Vector3((float)x, (float)y + 1, (float)z); dist = Math.Min(dist, 40); RequestNextFrameRendering();
    }

    void Rebuild()
    {
        cts?.Cancel(); var my = new CancellationTokenSource(); cts = my;
        var p = project; var a = atlas; int t = top;
        if (p == null || a == null) { lock (pending) { pending.Clear(); clearMeshes = true; } RequestNextFrameRendering(); return; }
        Status?.Invoke("Baue 3D-Ansicht …");
        var opt = Options(null);
        Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            List<MeshChunk> m; lock (buildLock) m = Mesher.Build(p, t, a, my.Token, opt);
            if (my.IsCancellationRequested) return;
            long tris = 0; foreach (var c in m) tris += c.I.Length / 3;
            var parts = m.GroupBy(c => c.Region).Select(g => ("r:" + g.Key, g.ToList())).ToList();
            lock (pending) { pending.Clear(); clearMeshes = true; pending.AddRange(parts!); }
            QueueAtlas(a);
            Dispatcher.UIThread.Post(() =>
            {
                if (cts != my) return;
                Status?.Invoke($"3D: {tris:N0} Dreiecke in {sw.ElapsedMilliseconds:N0} ms");
                RebuildExtras(true); RequestNextFrameRendering();
            });
        }, my.Token);
    }

    MeshOptions? Options(HashSet<int>? regions)
    {
        var p = project; var lv = live;
        if (!progress || lv == null || !lv.Known || p == null) return regions == null ? null : new MeshOptions { Regions = regions };
        return new MeshOptions { Regions = regions, Status = (x, y, z, v) => lv.Status(p, x, y, z, v) };
    }

    void QueueAtlas(Atlas a)
    {
        int n; lock (a.Tiles) n = a.Tiles.Count;
        if (n == atlasTiles && atlasTiles > 0) return;
        var at = a.Build(); atlasTiles = n;
        lock (pending) pendingAtlas = at;
    }

    /* ---------- Live: Baufortschritt, Umgebung, Spieler ---------- */
    public void SetLive(LiveClient? lv, bool showProgress, bool showSurroundings)
    {
        bool full = lv != live || showProgress != progress;
        bool surr = showSurroundings != surroundings || lv != live;
        live = lv; progress = showProgress; surroundings = showSurroundings;
        if (full) Rebuild();
        else if (surr) RebuildSurroundings(null);
    }

    /// <summary>Nur die Bereiche neu bauen, in denen sich in der Welt etwas geändert hat.</summary>
    public void LiveChanged(bool all, List<(int x, int y, int z)> cells, List<(int cx, int cz)> chunks)
    {
        var p = project; var a = atlas; if (p == null || a == null) return;
        if (progress && all) Rebuild();
        else if (progress)
        {
            var regs = new HashSet<int>();
            foreach (var (x, y, z) in cells)
                for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)   // Nachbarn: Flächen am Rand ändern sich mit
                {
                    int xx = x + dx, zz = z + dz; if (xx < 0 || zz < 0 || xx >= p.W || zz >= p.D) continue;
                    regs.Add(Mesher.RegionOf(p.W, xx, zz));
                }
            // Umgebungs-Chunks im Projekt betreffen den Fortschritt dort, wo der Baubereich noch nicht gelesen ist
            foreach (var (cx, cz) in chunks)
                for (int x = cx * 16 - p.OriginX; x < cx * 16 - p.OriginX + 16; x += 8) for (int z = cz * 16 - p.OriginZ; z < cz * 16 - p.OriginZ + 16; z += 8)
                    if (x >= 0 && z >= 0 && x < p.W && z < p.D) regs.Add(Mesher.RegionOf(p.W, x, z));
            if (regs.Count > 0) RebuildRegions(regs);
        }
        if (surroundings && (chunks.Count > 0 || all)) RebuildSurroundings(all ? null : chunks);
    }

    void RebuildRegions(HashSet<int> regs)
    {
        var p = project; var a = atlas; if (p == null || a == null) return; int t = top; var opt = Options(regs);
        Task.Run(() =>
        {
            List<MeshChunk> m; lock (buildLock) m = Mesher.Build(p, t, a, default, opt);
            var byReg = m.GroupBy(c => c.Region).ToDictionary(g => g.Key, g => g.ToList());
            lock (pending) foreach (var r in regs) pending.Add(("r:" + r, byReg.TryGetValue(r, out var l) ? l : new List<MeshChunk>()));
            QueueAtlas(a);
            Dispatcher.UIThread.Post(RequestNextFrameRendering);
        });
    }

    /// <summary>Umgebung aus dem aktuellen Stand: je Chunk eigene Geometrie (der Baubereich selbst bleibt der Plan).</summary>
    void RebuildSurroundings(List<(int cx, int cz)>? only)
    {
        var p = project; var a = atlas; var lv = live; if (p == null || a == null) return;
        if (!surroundings || lv == null)
        {
            lock (pending) foreach (var k in meshes.Keys.Where(k => k.StartsWith("c:")).ToList()) pending.Add((k, null));
            Dispatcher.UIThread.Post(RequestNextFrameRendering); return;
        }
        var list = only ?? lv.Chunks.Keys.ToList();
        Task.Run(() =>
        {
            foreach (var (cx, cz) in list)
            {
                if (!lv.Chunks.TryGetValue((cx, cz), out var c)) { lock (pending) pending.Add(($"c:{cx},{cz}", null)); continue; }
                var cells = (ushort[])c.Data.Clone();
                // Felder im Baubereich weglassen: dort zeigt die Ansicht den Plan bzw. den Fortschritt
                int px0 = p.OriginX, pz0 = p.OriginZ, py0 = p.OriginY;
                for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
                {
                    int wx = cx * 16 + x - px0, wz = cz * 16 + z - pz0; if (wx < 0 || wz < 0 || wx >= p.W || wz >= p.D) continue;
                    for (int y = 0; y < c.H; y++) { int wy = c.Y0 + y - py0; if (wy >= 0 && wy < p.H) cells[(y * 16 + z) * 16 + x] = 0; }
                }
                var cp = new Project { W = 16, D = 16, H = c.H, Cells = cells };
                var opt = new MeshOptions { UseOffset = true, OffX = cx * 16 - p.OriginX - p.W / 2f, OffZ = cz * 16 - p.OriginZ - p.D / 2f, OffY = c.Y0 - p.OriginY };
                List<MeshChunk> m; lock (buildLock) m = Mesher.Build(cp, c.H - 1, a, default, opt);
                lock (pending) pending.Add(($"c:{cx},{cz}", m));
            }
            QueueAtlas(a);
            Dispatcher.UIThread.Post(RequestNextFrameRendering);
        });
    }

    readonly Dictionary<uint, int> solidTiles = new();
    int Solid(uint rgb)
    {
        if (atlas == null) return 0;
        if (solidTiles.TryGetValue(rgb, out var t)) return t;
        var px = new byte[Atlas.T * Atlas.T * 4];
        for (int i = 0; i < Atlas.T * Atlas.T; i++) { px[i * 4] = (byte)(rgb >> 16); px[i * 4 + 1] = (byte)(rgb >> 8); px[i * 4 + 2] = (byte)rgb; px[i * 4 + 3] = 255; }
        return solidTiles[rgb] = atlas.TileOf(px);
    }

    public static uint PlayerColor(string name)
    {
        uint h = 2166136261; foreach (var ch in name) h = (h ^ ch) * 16777619;
        double hue = h % 360 / 60.0; double c = .62, xx = c * (1 - Math.Abs(hue % 2 - 1)), m = .2;
        var (r, g, b) = hue < 1 ? (c, xx, 0.0) : hue < 2 ? (xx, c, 0.0) : hue < 3 ? (0.0, c, xx) : hue < 4 ? (0.0, xx, c) : hue < 5 ? (xx, 0.0, c) : (c, 0.0, xx);
        return (uint)((r + m) * 255) << 16 | (uint)((g + m) * 255) << 8 | (uint)((b + m) * 255);
    }

    /// <summary>Quader als Flächen (Szenen-Koordinaten).</summary>
    static void AddBox(List<float> V, List<uint> I, float x0, float y0, float z0, float x1, float y1, float z1, int tile, float flag, bool top = true, bool bottom = true)
    {
        void Q(float[] c, int dir)
        {
            uint b = (uint)(V.Count / Mesher.Stride); float[] uv = { 0, 1, 1, 1, 1, 0, 0, 0 };
            for (int k = 0; k < 4; k++) { V.Add(c[k * 3]); V.Add(c[k * 3 + 1]); V.Add(c[k * 3 + 2]); V.Add(uv[k * 2]); V.Add(uv[k * 2 + 1]); V.Add(tile * 8 + dir); V.Add(flag); }
            I.Add(b); I.Add(b + 1); I.Add(b + 2); I.Add(b); I.Add(b + 2); I.Add(b + 3);
        }
        Q(new[] { x1, y1, z1, x1, y1, z0, x1, y0, z0, x1, y0, z1 }, 0); Q(new[] { x0, y1, z0, x0, y1, z1, x0, y0, z1, x0, y0, z0 }, 1);
        if (top) Q(new[] { x0, y1, z0, x1, y1, z0, x1, y1, z1, x0, y1, z1 }, 2); if (bottom) Q(new[] { x0, y0, z1, x1, y0, z1, x1, y0, z0, x0, y0, z0 }, 3);
        Q(new[] { x0, y1, z1, x1, y1, z1, x1, y0, z1, x0, y0, z1 }, 4); Q(new[] { x1, y1, z0, x0, y1, z0, x0, y0, z0, x1, y0, z0 }, 5);
    }

    public void SetPlayers() => RebuildExtras(false);

    public void SetGuides(PlanData? pl, bool show) { guidesPlan = pl; showGuides = show; RebuildExtras(true); }

    /// <summary>Spieler (und auf Wunsch die Hilfslinien) als eigene kleine Geometrie.</summary>
    void RebuildExtras(bool guides)
    {
        var p = project; if (p == null || atlas == null) return;
        float ox = -p.W / 2f, oz = -p.D / 2f;
        var lv = live;
        {
            var V = new List<float>(); var I = new List<uint>();
            if (lv != null)
                foreach (var q in lv.Players.Where(q => q.Dim == lv.Dim))
                {
                    float x = (float)(q.X - p.OriginX) + ox, y = (float)(q.Y - p.OriginY), z = (float)(q.Z - p.OriginZ) + oz; int t = Solid(PlayerColor(q.Name));
                    AddBox(V, I, x - .3f, y, z - .3f, x + .3f, y + 1.45f, z + .3f, t, 0);
                    AddBox(V, I, x - .25f, y + 1.45f, z - .25f, x + .25f, y + 1.95f, z + .25f, Solid(0xE8C39E), 0);   // Kopf
                    AddBox(V, I, x - .06f, y + 2.4f, z - .06f, x + .06f, y + 30f, z + .06f, t, 3, false, false);     // Markierung, von weit sichtbar
                }
            var m = new MeshChunk { V = V.ToArray(), I = I.ToArray() };
            lock (pending) pending.Add(("players", V.Count > 0 ? new List<MeshChunk> { m } : null));
        }
        if (guides)
        {
            var V = new List<float>(); var I = new List<uint>();
            if (showGuides && guidesPlan != null)
                foreach (var g in guidesPlan.Guides.Where(g => g.On))
                {
                    uint col; try { var c = Avalonia.Media.Color.Parse(g.Color); col = (uint)c.R << 16 | (uint)c.G << 8 | c.B; } catch { col = 0x7ee08f; }
                    int t = Solid(col); float y0 = g.Y, y1 = g.Y + g.H;
                    foreach (var (x, z) in g.Cells()) AddBox(V, I, x + ox, y0, z + oz, x + 1 + ox, y1, z + 1 + oz, t, 3);
                }
            lock (pending) pending.Add(("guides", V.Count > 0 ? new List<MeshChunk> { new() { V = V.ToArray(), I = I.ToArray(), Translucent = true } } : null));
        }
        QueueAtlas(atlas);
        RequestNextFrameRendering();
    }


    /* ---------- OpenGL ---------- */
    protected override void OnOpenGlInit(GlInterface gi)
    {
        Console.Error.WriteLine($"[3D] OpenGL {GlVersion.Type} {GlVersion.Major}.{GlVersion.Minor}");
        gl = GL.GetApi(gi.GetProcAddress);
        bool es = GlVersion.Type == GlProfileType.OpenGLES;
        string head = es ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";
        string vs = head + @"
layout(location=0) in vec3 pos; layout(location=1) in vec2 tuv; layout(location=2) in float td; layout(location=3) in float fl;
uniform mat4 mvp; uniform vec3 cam;
out vec2 vUv; out float vTd; out float vDist; out float vFl;
void main(){ vUv=tuv; vTd=td; vFl=fl; vDist=distance(pos,cam); gl_Position=mvp*vec4(pos,1.0); }";
        string fs = head + @"
uniform sampler2D atlas; uniform sampler2D avgTex; uniform float cols; uniform float trans; uniform float fogNear; uniform float fogFar; uniform vec3 sky;
in vec2 vUv; in float vTd; in float vDist; in float vFl; out vec4 outColor;
void main(){
  float t=floor((vTd+0.5)/8.0); float d=floor(vTd+0.5)-t*8.0;
  float sh = d<1.5?0.6:(d<2.5?1.0:(d<3.5?0.5:(d<5.5?0.8:0.9)));
  vec2 f=clamp(fract(vUv),0.002,0.998); vec2 o=vec2(mod(t,cols),floor(t/cols));
  vec4 c=texture(atlas,(o+vec2(f.x,1.0-f.y))/cols); if(trans<0.5 ? c.a<0.5 : c.a<0.02) discard;
  // weit weg: Durchschnittsfarbe der Kachel statt Pixelrauschen
  float tpp=16.0*max(length(dFdx(vUv)),length(dFdy(vUv)));
  vec3 avg=texelFetch(avgTex,ivec2(int(o.x),int(o.y)),0).rgb;
  c.rgb=mix(c.rgb,avg,clamp((tpp-0.8)/2.5,0.0,1.0));
  float fog=clamp((vDist-fogNear)/(fogFar-fogNear),0.0,1.0);
  float a = trans<0.5 ? 1.0 : max(c.a,0.45);
  // Baufortschritt: fehlt = hellblau durchscheinend, falsch = rot; Hilfslinien/Farben halb durchsichtig
  if (vFl>0.5 && vFl<1.5) { c.rgb=mix(c.rgb,vec3(0.49,0.78,1.0),0.55); a=0.32; }
  else if (vFl>1.5 && vFl<2.5) { c.rgb=mix(c.rgb,vec3(1.0,0.23,0.23),0.6); a=0.55; }
  else if (vFl>2.5) { a = trans<0.5 ? 1.0 : 0.55; }
  outColor=vec4(mix(c.rgb*sh,sky,fog), a);
}";
        try { prog = Link(vs, fs); } catch (Exception ex) { Console.Error.WriteLine("[3D] " + ex.Message); throw; }
        uMvp = gl.GetUniformLocation(prog, "mvp"); uCols = gl.GetUniformLocation(prog, "cols"); uCam = gl.GetUniformLocation(prog, "cam");
        uFogNear = gl.GetUniformLocation(prog, "fogNear"); uFogFar = gl.GetUniformLocation(prog, "fogFar"); uSky = gl.GetUniformLocation(prog, "sky");
        atlasTex = gl.GenTexture(); avgTex = gl.GenTexture();
        uAtlas = gl.GetUniformLocation(prog, "atlas"); uTrans = gl.GetUniformLocation(prog, "trans"); uAvg = gl.GetUniformLocation(prog, "avgTex");
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
        foreach (var l in meshes.Values) foreach (var m in l) { gl.DeleteVertexArray(m.vao); gl.DeleteBuffer(m.vbo); gl.DeleteBuffer(m.ebo); }
        meshes.Clear(); gl.DeleteProgram(prog); gl.DeleteTexture(atlasTex); gl.DeleteTexture(avgTex); gl = null;
    }

    unsafe void Upload()
    {
        List<(string key, List<MeshChunk>? chunks)> m; (byte[] px, int cols)? at; bool clr;
        lock (pending) { m = pending.ToList(); pending.Clear(); at = pendingAtlas; clr = clearMeshes; pendingAtlas = null; clearMeshes = false; }
        void Drop(string k) { if (meshes.Remove(k, out var l)) foreach (var x in l) { gl!.DeleteVertexArray(x.vao); gl.DeleteBuffer(x.vbo); gl.DeleteBuffer(x.ebo); } }
        if (clr) foreach (var k in meshes.Keys.Where(k => k.StartsWith("r:")).ToList()) Drop(k);
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
        foreach (var (key, chunks) in m)
        {
            Drop(key); if (chunks == null) continue;
            var list = new List<(uint, uint, uint, int, bool)>();
            foreach (var c in chunks)
            {
            if (c.I.Length == 0) continue;
            uint vao = gl!.GenVertexArray(); gl.BindVertexArray(vao);
            uint vbo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            fixed (float* p = c.V) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(c.V.Length * 4), p, BufferUsageARB.StaticDraw);
            uint ebo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
            fixed (uint* p = c.I) gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(c.I.Length * 4), p, BufferUsageARB.StaticDraw);
            uint st = Mesher.Stride * 4;
            gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, st, (void*)0);
            gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, st, (void*)12);
            gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, st, (void*)20);
            gl.EnableVertexAttribArray(3); gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, st, (void*)24);
            list.Add((vao, vbo, ebo, c.I.Length, c.Translucent));
            }
            meshes[key] = list;
        }
        gl!.BindVertexArray(0);
    }

    Vector3 CamPos() => target + new Vector3(MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Sin(yaw) * MathF.Cos(pitch)) * dist;

    /* ---------- Flugmodus: Kamera frei im Raum, wie in Minecraft im Zuschauermodus ---------- */
    public bool Fly { get; private set; }
    public float Speed { get; set; } = 8;
    /// <summary>Maus-Empfindlichkeit (1 = normal) für Drehen, Verschieben und Zoomen.</summary>
    public float MouseSens { get; set; } = 1;              // Blöcke pro Sekunde im Flugmodus
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
        // erst alles Feste, dann Wasser, Eis und farbiges Glas halb durchsichtig darüber
        gl.Uniform1(uTrans, 0f);
        foreach (var l in meshes.Values) foreach (var m in l) if (!m.trans) { gl.BindVertexArray(m.vao); gl.DrawElements(PrimitiveType.Triangles, (uint)m.count, DrawElementsType.UnsignedInt, (void*)0); }
        gl.Enable(EnableCap.Blend); gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); gl.DepthMask(false);
        gl.Uniform1(uTrans, 1f);
        foreach (var l in meshes.Values) foreach (var m in l) if (m.trans) { gl.BindVertexArray(m.vao); gl.DrawElements(PrimitiveType.Triangles, (uint)m.count, DrawElementsType.UnsignedInt, (void*)0); }
        gl.DepthMask(true); gl.Disable(EnableCap.Blend);
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
                float r = 0.0022f * MouseSens; lookYaw += (float)d.X * r; lookPitch = Math.Clamp(lookPitch - (float)d.Y * r, -1.55f, 1.55f);
            }
            else if (panning)
            {
                var fwd = Forward(); var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY)); var up = Vector3.Cross(right, fwd);
                float k = 2 * dist * MathF.Tan(35 * MathF.PI / 180) / (float)Math.Max(1, Bounds.Height) * MouseSens; target += (-right * (float)d.X + up * (float)d.Y) * k;
            }
            else { float r = 0.003f * MouseSens; yaw += (float)d.X * r; pitch = Math.Clamp(pitch + (float)d.Y * r, -1.5f, 1.5f); }
            RequestNextFrameRendering();
        }
        else Pick(pos);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        float wd = Math.Clamp((float)e.Delta.Y, -3, 3); // Touchpads liefern teils große Sprünge
        if (Fly) eye += Forward() * wd * Math.Max(1, Speed * 0.15f) * MouseSens;
        else dist = Math.Clamp(dist * MathF.Exp(-wd * 0.06f * MouseSens), 2, 6000);
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
