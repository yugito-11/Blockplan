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
    GL? gl; uint prog, atlasTex, avgTex; int uAtlas, uAvg, uTrans; int uMvp, uCols, uFogNear, uFogFar, uCam, uSky, uNight, uSun, uSunOn, uHiOn;
    int uShaderOn, uLmvp, uShadowTex, uShadowRes, uTime, uViewport, uZenith, uLightTex, uLightOn, uLightSize, uProjOff;
    uint lightTex3; byte[]? lightUploaded; bool lightTexOk; int uShadowTexel; float shadowTexelSize;
    uint shadowProg, skyProg, skyVao, skyVbo, shadowFbo, shadowTex; int shadowSize; bool shadersOk, shadowDirty = true; Vector3 shadowSun; Matrix4x4 lightVP;
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

    static readonly Vector3 Sky = new(0.47f, 0.65f, 1.0f), NightSky = new(0.035f, 0.05f, 0.11f);
    /// <summary>Fotomodus: Sonnenrichtung statt fester Helligkeit je Seite; Himmel nach Tageszeit.</summary>
    bool sunOn; Vector3 sunDir = new(0.4f, 0.8f, 0.3f); Vector3? skyOverride;
    Vector3 SkyNow() => skyOverride ?? (night && lightMap != null ? NightSky : Sky);

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
        bool other = p != project; project = p; atlas = a; top = topY;
        if (p != null && refit) Fit();
        if (other) { lightMap = null; spawnSpots = new(); hidden = null; hiY = -1; }
        if ((night || Shaders) && p != null && (other || lightMap == null || lightTop != topY)) ComputeLight(); else Rebuild();
    }

    public void SetTop(int topY) => SetTop(topY, hiY);
    /// <summary>Höhe und hervorgehobene Ebene zusammen setzen (nur einmal neu bauen).</summary>
    public void SetTop(int topY, int highlight)
    {
        if (topY == top && highlight == hiY) return;
        bool topChanged = topY != top; top = topY; hiY = highlight;
        if ((night || Shaders) && topChanged) ComputeLight(); else Rebuild();
    }
    public void ShowSpawns(bool on) { showSpawns = on; RebuildExtras(false); RequestNextFrameRendering(); }

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
        if (Fly) SetFly(false); if (Walk) SetWalk(false);
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
                var w = buildWaiters; buildWaiters = new(); foreach (var t2 in w) t2.TrySetResult();
            });
        }, my.Token);
    }

    List<TaskCompletionSource> buildWaiters = new();
    /// <summary>Wartet, bis die nächste vollständige 3D-Geometrie fertig ist (für Videobilder).</summary>
    public Task NextBuild() { var t = new TaskCompletionSource(); buildWaiters.Add(t); return t.Task; }

    MeshOptions? Options(HashSet<int>? regions)
    {
        var p = project; var lv = live;
        var o = new MeshOptions { Regions = regions, HighlightY = hiY, ClipAxis = clipAxis, ClipPos = clipPos, ClipKeepHigh = clipHigh, Light = night || Shaders ? lightMap : null, Hidden = hidden };
        if (progress && hidden == null && lv != null && lv.Known && p != null) o.Status = (x, y, z, v) => lv.Status(p, x, y, z, v);
        return o;
    }

    /* ---------- Bau-Anleitung, Schnitt, Nacht, Zeitraffer ---------- */
    int hiY = -1, clipAxis, clipPos; bool clipHigh, night, showSpawns; byte[]? lightMap; Func<long, bool>? hidden;
    List<(int x, int y, int z)> spawnSpots = new();
    public int SpawnCount => spawnSpots.Count;
    public bool Night => night;
    public event Action? LightReady;

    /// <summary>Ebene, die in der Bau-Anleitung hervorgehoben wird (-1 = keine).</summary>
    public void SetHighlight(int y) { if (y == hiY) return; hiY = y; Rebuild(); }

    /// <summary>Schnitt: Achse 0 aus, 1 = X (Blick von Osten/Westen), 2 = Z; pos in Projekt-Koordinaten.</summary>
    public void SetClip(int axis, int pos, bool keepHigh)
    {
        if (axis == clipAxis && pos == clipPos && keepHigh == clipHigh) return;
        clipAxis = axis; clipPos = pos; clipHigh = keepHigh; Rebuild();
    }

    /// <summary>Zeitraffer: Felder, die zu diesem Zeitpunkt noch nicht gebaut waren, weglassen.</summary>
    public void SetHidden(Func<long, bool>? h) { hidden = h; Rebuild(); }

    /// <summary>Nachtansicht: Licht berechnen (im Hintergrund), dann neu bauen.</summary>
    public void SetNight(bool on, bool spawns)
    {
        bool lightChanged = on != night; night = on; showSpawns = spawns;
        if (!on && !Shaders) { spawnSpots = new(); if (lightChanged) Rebuild(); else RebuildExtras(false); return; }
        if (!on) { spawnSpots = new(); Rebuild(); return; }
        ComputeLight();
    }

    /// <summary>Licht (und Spawn-Stellen) im Hintergrund berechnen, dann neu bauen.</summary>
    void ComputeLight()
    {
        var p = project; int t = top; if (p == null) return;
        Status?.Invoke("Berechne Licht …");
        Task.Run(() =>
        {
            var L = Lighting.Compute(p, t); var sp = Lighting.SpawnSpots(p, L, t);
            if (Environment.GetEnvironmentVariable("BLOCKPLAN_DEBUGLIGHT") is { } dl) foreach (var c in dl.Split(';')) { var q = c.Split(',').Select(int.Parse).ToArray(); var i = p.Index(q[0], q[1], q[2]); Console.Error.WriteLine($"[Licht] {c}: Block {Blocks.Get(p.Cells[i] == 0 ? 0 : p.Cells[i])?.Key} Himmel {L[i] >> 4} Block {L[i] & 15}"); }
            Dispatcher.UIThread.Post(() =>
            {
                if (project != p || !(night || Shaders)) return;
                lightMap = L; spawnSpots = night ? sp : new(); lightTop = t; Rebuild(); LightReady?.Invoke();
            });
        });
    }
    int lightTop = -1;

    /* ---------- Shader: Schatten, Himmel, Wasser, Farben ---------- */
    public bool Shaders { get; private set; }
    bool ShaderActive => Shaders && shadersOk;
    /// <summary>Sonnenstand in Grad (5 = Morgen im Osten, 90 = Mittag, 175 = Abend im Westen).</summary>
    public float SunAngle { get => sunAngle; set { sunAngle = Math.Clamp(value, 3, 177); shadowDirty = true; RequestNextFrameRendering(); } }
    float sunAngle = 55;
    DispatcherTimer? animTimer;
    public bool ShadersAvailable => shadersOk;

    public void SetShaders(bool on)
    {
        if (on == Shaders) return;
        Shaders = on; shadowDirty = true;
        if (on && (lightMap == null || lightTop != top)) ComputeLight(); else Rebuild();
        animTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => { if (IsVisible) RequestNextFrameRendering(); });
        if (on) animTimer.Start(); else animTimer.Stop();
    }

    static Vector3 SunFromAngle(float deg)
    {
        float a = deg * MathF.PI / 180;
        return Vector3.Normalize(new Vector3(MathF.Cos(a) * 0.9f, MathF.Sin(a), 0.38f));
    }

    /// <summary>Himmelsfarben nach Sonnenstand: Horizont und Zenit.</summary>
    static (Vector3 horizon, Vector3 zenith) SkyColors(Vector3 sun, bool isNight)
    {
        if (isNight) return (new Vector3(0.05f, 0.07f, 0.14f), new Vector3(0.01f, 0.015f, 0.04f));
        float e = Math.Clamp(sun.Y * 2.2f, 0, 1);
        var hz = Vector3.Lerp(new Vector3(1.0f, 0.60f, 0.42f), new Vector3(0.70f, 0.82f, 1.0f), e);
        var zn = Vector3.Lerp(new Vector3(0.30f, 0.38f, 0.72f), new Vector3(0.22f, 0.47f, 0.95f), e);
        return (hz, zn);
    }

    /// <summary>Licht je Feld als 3D-Textur (Himmel, Block), damit es auf den Flächen weich verläuft.</summary>
    unsafe void UploadLight(byte[] L, Project p)
    {
        var gl = this.gl!; lightUploaded = L; lightTexOk = false;
        gl.GetInteger(GetPName.Max3DTextureSize, out int max3);
        // sehr große Bauten: in X und Z gröber (je 2×2 Felder das hellste), damit es in jede Grafikkarte passt
        int s = 1; while (((long)p.W * p.D * p.H) / ((long)s * s) > 48_000_000L || (p.W + s - 1) / s > max3 || (p.D + s - 1) / s > max3) s++;
        if (p.H > max3 || s > 4) return;   // zu groß: Licht je Fläche wie bisher
        int TW = (p.W + s - 1) / s, TD = (p.D + s - 1) / s, TH = p.H;
        var px = new byte[(long)TW * TD * TH * 2];
        long WD = (long)p.W * p.D;
        System.Threading.Tasks.Parallel.For(0, TH, y =>
        {
            for (int tz = 0; tz < TD; tz++) for (int tx = 0; tx < TW; tx++)
            {
                int sk = 0, bk = 0;
                for (int dz = 0; dz < s; dz++) for (int dx = 0; dx < s; dx++)
                {
                    int x = tx * s + dx, z = tz * s + dz; if (x >= p.W || z >= p.D) continue;
                    int v = L[y * WD + (long)z * p.W + x]; sk = Math.Max(sk, v >> 4); bk = Math.Max(bk, v & 15);
                }
                long o = (((long)y * TD + tz) * TW + tx) * 2; px[o] = (byte)(sk * 17); px[o + 1] = (byte)(bk * 17);
            }
        });
        if (lightTex3 == 0) lightTex3 = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture3D, lightTex3);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        while (gl.GetError() != GLEnum.NoError) { }
        fixed (byte* d = px) gl.TexImage3D(TextureTarget.Texture3D, 0, InternalFormat.RG8, (uint)TW, (uint)TD, (uint)TH, 0, PixelFormat.RG, PixelType.UnsignedByte, d);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        gl.BindTexture(TextureTarget.Texture3D, 0);
        lightTexOk = gl.GetError() == GLEnum.NoError;
    }

    /// <summary>Schattenkarte aus Sicht der Sonne zeichnen (nur feste Blöcke).</summary>
    unsafe void RenderShadow(Vector3 sunN, int res)
    {
        var gl = this.gl!; var p = project; if (p == null) return;
        if (shadowFbo == 0 || shadowSize != res)
        {
            if (shadowFbo != 0) { gl.DeleteFramebuffer(shadowFbo); gl.DeleteTexture(shadowTex); }
            shadowTex = gl.GenTexture(); gl.BindTexture(TextureTarget.Texture2D, shadowTex);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, (uint)res, (uint)res, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            shadowFbo = gl.GenFramebuffer(); gl.BindFramebuffer(FramebufferTarget.Framebuffer, shadowFbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, shadowTex, 0);
            var none = GLEnum.None; gl.DrawBuffers(1, &none); gl.ReadBuffer(GLEnum.None);
            shadowSize = res;
        }
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, shadowFbo);
        gl.Viewport(0, 0, (uint)res, (uint)res);
        gl.Clear(ClearBufferMask.DepthBufferBit);
        gl.Enable(EnableCap.DepthTest); gl.DepthFunc(DepthFunction.Lequal); gl.DepthMask(true); gl.Disable(EnableCap.Blend);
        var center = new Vector3(0, p.H / 2f, 0);
        float r = 0.5f * MathF.Sqrt((float)p.W * p.W + (float)p.H * p.H + (float)p.D * p.D) + 12;
        var up = MathF.Abs(sunN.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(center + sunN * r * 2, center, up);
        var proj = Matrix4x4.CreateOrthographic(2 * r, 2 * r, 0.1f, 4 * r);
        lightVP = view * proj; shadowTexelSize = 2 * r / res;
        gl.UseProgram(shadowProg);
        var m = lightVP; gl.UniformMatrix4(gl.GetUniformLocation(shadowProg, "lmvp"), 1, false, (float*)&m);
        gl.Uniform1(gl.GetUniformLocation(shadowProg, "cols"), (float)atlasCols);
        gl.ActiveTexture(TextureUnit.Texture0); gl.BindTexture(TextureTarget.Texture2D, atlasTex); gl.Uniform1(gl.GetUniformLocation(shadowProg, "atlas"), 0);
        foreach (var (k, l) in meshes)
        {
            if (k is "guides" or "players" or "spawn") continue;
            foreach (var mm in l) if (!mm.trans) { gl.BindVertexArray(mm.vao); gl.DrawElements(PrimitiveType.Triangles, (uint)mm.count, DrawElementsType.UnsignedInt, (void*)0); }
        }
        gl.BindVertexArray(0);
        shadowDirty = false; shadowSun = sunN;
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

    /// <summary>Flacher Strich am Boden von (x0,z0) nach (x1,z1), Breite w (für Spawn-Kreuze).</summary>
    static void AddFlat(List<float> V, List<uint> I, float x0, float z0, float x1, float z1, float y, float w, int tile)
    {
        float dx = x1 - x0, dz = z1 - z0, l = MathF.Sqrt(dx * dx + dz * dz), nx = -dz / l * w, nz = dx / l * w;
        uint b = (uint)(V.Count / Mesher.Stride);
        float[] c = { x0 + nx, z0 + nz, x1 + nx, z1 + nz, x1 - nx, z1 - nz, x0 - nx, z0 - nz };
        for (int k = 0; k < 4; k++) { V.Add(c[k * 2]); V.Add(y); V.Add(c[k * 2 + 1]); V.Add(0); V.Add(0); V.Add(tile * 8 + 2); V.Add(5); V.Add(0); }
        I.Add(b); I.Add(b + 1); I.Add(b + 2); I.Add(b); I.Add(b + 2); I.Add(b + 3);
    }

    /// <summary>Quader als Flächen (Szenen-Koordinaten).</summary>
    static void AddBox(List<float> V, List<uint> I, float x0, float y0, float z0, float x1, float y1, float z1, int tile, float flag, bool top = true, bool bottom = true)
    {
        void Q(float[] c, int dir)
        {
            uint b = (uint)(V.Count / Mesher.Stride); float[] uv = { 0, 1, 1, 1, 1, 0, 0, 0 };
            for (int k = 0; k < 4; k++) { V.Add(c[k * 3]); V.Add(c[k * 3 + 1]); V.Add(c[k * 3 + 2]); V.Add(uv[k * 2]); V.Add(uv[k * 2 + 1]); V.Add(tile * 8 + dir); V.Add(flag); V.Add(Mesher.DayLight); }
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
            // Nacht: Stellen, an denen Monster erscheinen können, als rote Kreuze am Boden
            var SV = new List<float>(); var SI = new List<uint>();
            int red = night && showSpawns && spawnSpots.Count > 0 ? Solid(0xff3344) : 0;
            if (night && showSpawns)
                foreach (var (x, y, z) in spawnSpots)
                {
                    if (y > top || (clipAxis != 0 && !(clipHigh ? (clipAxis == 1 ? x : z) >= clipPos : (clipAxis == 1 ? x : z) <= clipPos))) continue;
                    float X = x + ox, Z = z + oz, Y = y + 0.03f, a = .18f, b = .82f, w = .09f;
                    AddFlat(SV, SI, X + a, Z + a, X + b, Z + b, Y, w, red); AddFlat(SV, SI, X + a, Z + b, X + b, Z + a, Y, w, red);
                }
            lock (pending) pending.Add(("spawn", SV.Count > 0 ? new List<MeshChunk> { new() { V = SV.ToArray(), I = SI.ToArray() } } : null));
        }
        if (guides)
        {
            var V = new List<float>(); var I = new List<uint>();
            var SV = new List<float>(); var SI = new List<uint>();
            if (showGuides && guidesPlan != null)
                foreach (var g in guidesPlan.Guides.Where(g => g.On))
                {
                    uint col; try { var c = Avalonia.Media.Color.Parse(g.Color); col = (uint)c.R << 16 | (uint)c.G << 8 | c.B; } catch { col = 0x7ee08f; }
                    bool sel = g.Id == SelectedGuide;   // ausgewählt: kräftig und undurchsichtig
                    int t = Solid(col); float y0 = g.Y, y1 = g.Y + g.H;
                    foreach (var (x, z) in g.Cells(p)) AddBox(sel ? SV : V, sel ? SI : I, x + ox, y0, z + oz, x + 1 + ox, y1, z + 1 + oz, t, 3);
                }
            var list = new List<MeshChunk>();
            if (V.Count > 0) list.Add(new() { V = V.ToArray(), I = I.ToArray(), Translucent = true });
            if (SV.Count > 0) list.Add(new() { V = SV.ToArray(), I = SI.ToArray() });
            lock (pending) pending.Add(("guides", list.Count > 0 ? list : null));
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
layout(location=0) in vec3 pos; layout(location=1) in vec2 tuv; layout(location=2) in float td; layout(location=3) in float fl; layout(location=4) in float li;
uniform mat4 mvp; uniform vec3 cam; uniform mat4 lmvp; uniform float shadowTexel;
out vec2 vUv; out float vTd; out float vDist; out float vFl; out float vLi; out vec3 vPos; out vec4 vLs;
void main(){
  vUv=tuv; vTd=td; vFl=fl; vLi=li; vPos=pos;
  float t=floor((td+0.5)/8.0); float d=floor(td+0.5)-t*8.0;
  vec3 n = d<0.5?vec3(1,0,0):(d<1.5?vec3(-1,0,0):(d<2.5?vec3(0,1,0):(d<3.5?vec3(0,-1,0):(d<4.5?vec3(0,0,1):(d<5.5?vec3(0,0,-1):vec3(0,1,0))))));
  vLs=lmvp*vec4(pos + n*shadowTexel*1.8, 1.0);   // etwas vor der Fläche nachschauen: keine Streifen
  vDist=distance(pos,cam); gl_Position=mvp*vec4(pos,1.0);
}";
        string fs = head + @"
uniform sampler2D atlas; uniform sampler2D avgTex; uniform float cols; uniform float trans; uniform float fogNear; uniform float fogFar; uniform vec3 sky;
uniform float night; uniform vec3 sun; uniform float sunOn; uniform float hiOn;
uniform float shaderOn; uniform sampler2D shadowTex; uniform float shadowRes; uniform float time; uniform vec2 viewport; uniform vec3 cam; uniform vec3 zenith;
uniform highp sampler3D lightTex; uniform float lightOn; uniform vec3 lightSize; uniform vec3 projOff;
in vec2 vUv; in float vTd; in float vDist; in float vFl; in float vLi; in vec3 vPos; in vec4 vLs; out vec4 outColor;
float bright(float l){ float x=l/15.0; return mix(x/(4.0-3.0*x), x, 0.35); }
float shadowAt(float ndl){
  vec3 p = vLs.xyz/vLs.w*0.5+0.5;
  if (p.x<0.0||p.x>1.0||p.y<0.0||p.y>1.0||p.z>1.0) return 1.0;
  float bias = 0.0002 + 0.0006*(1.0-ndl); float s = 0.0;
  for (int x=-1;x<=1;x++) for (int y=-1;y<=1;y++) s += (p.z-bias > texture(shadowTex, p.xy+vec2(float(x),float(y))/shadowRes).r) ? 0.0 : 1.0;
  return s/9.0;
}
vec3 aces(vec3 x){ return clamp((x*(2.51*x+0.03))/(x*(2.43*x+0.59)+0.14),0.0,1.0); }
void main(){
  float t=floor((vTd+0.5)/8.0); float d=floor(vTd+0.5)-t*8.0;
  float sh = d<1.5?0.6:(d<2.5?1.0:(d<3.5?0.5:(d<5.5?0.8:0.9)));
  if (sunOn>0.5 && d<5.5) {   // Fotomodus: Licht aus Richtung der Sonne
    vec3 n = d<0.5?vec3(1,0,0):(d<1.5?vec3(-1,0,0):(d<2.5?vec3(0,1,0):(d<3.5?vec3(0,-1,0):(d<4.5?vec3(0,0,1):vec3(0,0,-1)))));
    sh = 0.42 + 0.68*max(dot(n,normalize(sun)),0.0);
  }
  vec2 f=clamp(fract(vUv),0.002,0.998); int ti=int(vTd+0.5)/8, ic=int(cols+0.5); vec2 o=vec2(float(ti-(ti/ic)*ic), float(ti/ic));   // ganzzahlig: exakt auf jeder Grafikkarte
  // Mipmaps je Kachel: Ableitungen aus den nicht umgebrochenen Koordinaten, Rand je nach Stufe einrücken (kein Bluten)
  vec2 gx = vec2(dFdx(vUv.x), -dFdx(vUv.y))/cols, gy = vec2(dFdy(vUv.x), -dFdy(vUv.y))/cols;
  float lod = clamp(log2(max(max(length(gx),length(gy))*cols*16.0, 1e-6)), 0.0, 4.0);
  float hm = 0.5*exp2(lod)/16.0; vec2 g = clamp(vec2(f.x,1.0-f.y), hm, 1.0-hm);
  vec4 c=textureGrad(atlas,(o+g)/cols,gx,gy); if(trans<0.5 ? c.a<0.5 : c.a<0.02) discard;
  float fog=clamp((vDist-fogNear)/(fogFar-fogNear),0.0,1.0);
  float a = trans<0.5 ? 1.0 : max(c.a,0.45);
  // Baufortschritt: fehlt = hellblau durchscheinend, falsch = rot; Hilfslinien/Farben halb durchsichtig
  if (vFl>0.5 && vFl<1.5) { c.rgb=mix(c.rgb,vec3(0.49,0.78,1.0),0.55); a=0.32; }
  else if (vFl>1.5 && vFl<2.5) { c.rgb=mix(c.rgb,vec3(1.0,0.23,0.23),0.6); a=0.55; }
  else if (vFl>2.5 && vFl<3.5) { a = trans<0.5 ? 1.0 : 0.55; }
  else if (vFl>3.5 && vFl<4.5) { c.rgb = mix(c.rgb, vec3(1.0,0.84,0.25), 0.38) * 1.15; }   // Bau-Anleitung: aktuelle Ebene
  vec3 col = shaderOn>0.5 && (vFl<0.5 || vFl>5.5) ? c.rgb : c.rgb*sh;
  if (hiOn>0.5 && (vFl<0.5 || vFl>6.5)) col *= 0.55;   // Bau-Anleitung: Fertiges dunkler, damit die neue Ebene auffällt
  if (night>0.0 && !(shaderOn>0.5 && (vFl<0.5 || vFl>5.5))) {   // Nacht: Mondlicht von oben, warmes Licht von Fackeln & Co.
    float skyL=floor(vLi/16.0+0.001); float blk=vLi-skyL*16.0;
    vec3 L = max(vec3(0.20,0.25,0.42)*bright(skyL)+vec3(0.035), vec3(1.0,0.80,0.55)*bright(blk));
    col = mix(col, col*L, night);
  }
  if (shaderOn>0.5 && (vFl<0.5 || vFl>5.5)) {
    // Shader: Sonne mit Schatten, Himmelslicht (dunkler unter Dächern), warmes Blocklicht, Wasser mit Wellen
    vec3 sN = normalize(sun);
    vec3 n = d<0.5?vec3(1,0,0):(d<1.5?vec3(-1,0,0):(d<2.5?vec3(0,1,0):(d<3.5?vec3(0,-1,0):(d<4.5?vec3(0,0,1):(d<5.5?vec3(0,0,-1):vec3(0,1,0))))));
    bool water = vFl>5.5 && vFl<6.5;
    if (water && d>1.5 && d<2.5) n = normalize(vec3(sin(vPos.x*1.9+time*1.6)*0.07+sin(vPos.z*3.1-time*2.3)*0.04, 1.0, cos(vPos.z*1.7+time*1.3)*0.07+cos(vPos.x*2.7+time*1.9)*0.04));
    float ndl = max(dot(n,sN),0.0); if (d>5.5) ndl = 0.6;
    float shd = ndl>0.0 ? shadowAt(ndl) : 0.0;
    float skyL=floor(vLi/16.0+0.001); float blk=vLi-skyL*16.0;
    // weiches Licht: Lichtwerte der Felder davor, zwischen den Feldern verlaufend (dunklere Ecken wie im Spiel)
    vec3 lp = vPos + projOff + (d<5.5 ? n*0.5 : vec3(0.0));
    if (lightOn>0.5 && lp.x>=0.0 && lp.z>=0.0 && lp.x<=lightSize.x && lp.z<=lightSize.z && lp.y>=0.0) {
      // mehrere Proben ringsum: Licht verläuft weich und rund statt in Rauten
      vec3 t1 = abs(n.y)>0.5 ? vec3(1,0,0) : vec3(0,1,0); vec3 t2 = cross(n,t1);
      vec3 sc = vec3(1.0/lightSize.x, 1.0/lightSize.y, 1.0/lightSize.z);
      vec2 acc = 2.0*texture(lightTex, (lp).xzy*sc.xzy).rg;
      acc += texture(lightTex, (lp+t1*0.9).xzy*sc.xzy).rg + texture(lightTex, (lp-t1*0.9).xzy*sc.xzy).rg;
      acc += texture(lightTex, (lp+t2*0.9).xzy*sc.xzy).rg + texture(lightTex, (lp-t2*0.9).xzy*sc.xzy).rg;
      acc += texture(lightTex, (lp+n*0.8).xzy*sc.xzy).rg;
      vec2 lv = acc/7.0*15.0;
      skyL = lp.y>lightSize.y ? 15.0 : lv.x; blk = lv.y;
    }
    float day = 1.0 - night;
    vec3 sunCol = mix(vec3(1.0,0.58,0.36), vec3(1.0,0.96,0.90), clamp(sN.y*2.2,0.0,1.0));
    sunCol = mix(vec3(0.30,0.36,0.55)*0.5, sunCol*0.62, day);
    vec3 amb = mix(vec3(0.05,0.06,0.11), mix(vec3(0.60,0.66,0.80), vec3(0.78,0.66,0.58), 1.0-clamp(sN.y*2.0,0.0,1.0)), day);
    float side = 0.82 + 0.18*n.y;
    // wie im Spiel zählt das hellere: Himmelslicht oder Licht von Fackeln & Co.
    float bl = clamp(blk/15.0,0.0,1.0); float blkB = pow(bl, 2.4)*1.15;   // weicher Verlauf, sanftes Auslaufen
    vec3 ind = max(amb*bright(skyL), vec3(1.0,0.78,0.52)*blkB*mix(1.35,1.0,day));
    vec3 L = ind*side + sunCol*ndl*shd*clamp(skyL/8.0,0.0,1.0);
    col = c.rgb*L;
    // Lichtquellen selbst leuchten (Fackel, Laterne, Glowstone …)
    if (vFl>6.5) col = mix(col, c.rgb*vec3(1.15,1.05,0.9), mix(0.85,0.45,day));
    if (water) {
      vec3 v = normalize(vPos-cam); vec3 r = reflect(v,n);
      float fres = 0.10 + 0.90*pow(1.0-max(dot(-v,n),0.0),4.0);
      col = mix(col, vec3(0.06,0.24,0.50)*(amb*0.9+sunCol*0.55), 0.55);   // Tiefe: blauer, weniger durchsichtig
      vec3 skyRefl = mix(sky, zenith, clamp(r.y,0.0,1.0));
      col = mix(col, skyRefl*mix(0.25,1.0,day), clamp(fres,0.0,0.85));
      col += sunCol*pow(max(dot(r,sN),0.0),180.0)*2.0*shd;
      a = mix(0.78, 0.95, fres);
    }
    col = col*1.06/(1.0+0.12*col);   // sanft begrenzen, Texturfarben bleiben kräftig
    col = mix(vec3(dot(col,vec3(0.299,0.587,0.114))), col, 1.10);
  }
  else if (shaderOn>0.5) col = col*1.0/(1.0+0.1*col);
  if (vFl>4.5 && vFl<5.5) { col = vec3(1.0,0.2,0.25); a = 0.85; }   // Spawn-Stelle
  vec3 outc = mix(col,sky,fog);
  if (shaderOn>0.5) { vec2 uv = gl_FragCoord.xy/viewport; outc *= mix(0.80, 1.0, smoothstep(0.95, 0.35, length(uv-0.5))); }
  outColor=vec4(outc, a);
}";
        try { prog = Link(vs, fs); } catch (Exception ex) { Console.Error.WriteLine("[3D] " + ex.Message); throw; }
        try
        {
            shadowProg = Link(head + @"
layout(location=0) in vec3 pos; layout(location=1) in vec2 tuv; layout(location=2) in float td;
uniform mat4 lmvp; out vec2 vUv; out float vTd;
void main(){ vUv=tuv; vTd=td; gl_Position=lmvp*vec4(pos,1.0); }", head + @"
uniform sampler2D atlas; uniform float cols; in vec2 vUv; in float vTd; out vec4 o;
void main(){ float t=floor((vTd+0.5)/8.0); vec2 f=clamp(fract(vUv),0.002,0.998); int ti=int(vTd+0.5)/8, ic=int(cols+0.5); vec2 g=vec2(float(ti-(ti/ic)*ic), float(ti/ic));
  if (textureLod(atlas,(g+vec2(f.x,1.0-f.y))/cols,0.0).a<0.5) discard; o=vec4(1.0); }");
            skyProg = Link(head + @"
layout(location=0) in vec2 p; out vec2 vp; void main(){ vp=p; gl_Position=vec4(p,0.0,1.0); }", head + @"
uniform mat4 invVP; uniform vec3 sun; uniform vec3 horizon; uniform vec3 zenith; uniform float night; in vec2 vp; out vec4 o;
float h(vec3 p){ return fract(sin(dot(p,vec3(12.9898,78.233,45.164)))*43758.5453); }
void main(){
  vec4 a=invVP*vec4(vp,-1.0,1.0); vec4 b=invVP*vec4(vp,1.0,1.0); vec3 dir=normalize(b.xyz/b.w-a.xyz/a.w);
  float t=clamp(dir.y,0.0,1.0); vec3 col=mix(horizon,zenith,pow(t,0.55));
  if (dir.y<0.0) col=mix(horizon,horizon*0.82,clamp(-dir.y*3.0,0.0,1.0));
  vec3 sN=normalize(sun); float s=max(dot(dir,sN),0.0); float day=1.0-night;
  vec3 sc = mix(vec3(1.0,0.6,0.35), vec3(1.0,0.97,0.9), clamp(sN.y*2.2,0.0,1.0));
  col += sc*(pow(s,900.0)*4.0 + pow(s,14.0)*0.28)*day;
  col += vec3(0.85,0.9,1.0)*pow(s,1500.0)*2.0*night;   // Mond
  vec3 cell=floor(dir*260.0); if (night>0.5 && dir.y>0.0 && h(cell)>0.9965) col += vec3(0.9)*(0.5+0.5*h(cell+1.0));
  o=vec4(col,1.0);
}");
            skyVao = gl.GenVertexArray(); gl.BindVertexArray(skyVao); skyVbo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ArrayBuffer, skyVbo);
            float[] tri = { -1, -1, 3, -1, -1, 3 };
            unsafe { fixed (float* tp = tri) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(tri.Length * 4), tp, BufferUsageARB.StaticDraw); gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 8, (void*)0); }
            gl.BindVertexArray(0);
            shadersOk = true;
        }
        catch (Exception ex) { Console.Error.WriteLine("[3D] Shader nicht verfügbar: " + ex.Message); shadersOk = false; }
        uShaderOn = gl.GetUniformLocation(prog, "shaderOn"); uLmvp = gl.GetUniformLocation(prog, "lmvp"); uShadowTex = gl.GetUniformLocation(prog, "shadowTex");
        uLightTex = gl.GetUniformLocation(prog, "lightTex"); uLightOn = gl.GetUniformLocation(prog, "lightOn"); uLightSize = gl.GetUniformLocation(prog, "lightSize"); uProjOff = gl.GetUniformLocation(prog, "projOff");
        uShadowRes = gl.GetUniformLocation(prog, "shadowRes"); uShadowTexel = gl.GetUniformLocation(prog, "shadowTexel");
        // jede Textur-Art auf eigener Einheit: sonst stört die 3D-Lichttextur die Blocktexturen
        gl.UseProgram(prog); gl.Uniform1(uLightTex, 3); gl.Uniform1(uShadowTex, 2); gl.UseProgram(0); uTime = gl.GetUniformLocation(prog, "time"); uViewport = gl.GetUniformLocation(prog, "viewport"); uZenith = gl.GetUniformLocation(prog, "zenith");
        uMvp = gl.GetUniformLocation(prog, "mvp"); uCols = gl.GetUniformLocation(prog, "cols"); uCam = gl.GetUniformLocation(prog, "cam");
        uHiOn = gl.GetUniformLocation(prog, "hiOn"); uNight = gl.GetUniformLocation(prog, "night"); uSun = gl.GetUniformLocation(prog, "sun"); uSunOn = gl.GetUniformLocation(prog, "sunOn");
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
        meshes.Clear(); gl.DeleteProgram(prog);
        if (shadowFbo != 0) { gl.DeleteFramebuffer(shadowFbo); gl.DeleteTexture(shadowTex); shadowFbo = 0; }
        if (lightTex3 != 0) { gl.DeleteTexture(lightTex3); lightTex3 = 0; lightUploaded = null; }
        if (msaaFbo != 0) { gl.DeleteFramebuffer(msaaFbo); gl.DeleteRenderbuffer(msaaColor); gl.DeleteRenderbuffer(msaaDepth); msaaFbo = 0; }
        if (shadersOk) { gl.DeleteProgram(shadowProg); gl.DeleteProgram(skyProg); gl.DeleteVertexArray(skyVao); gl.DeleteBuffer(skyVbo); } gl.DeleteTexture(atlasTex); gl.DeleteTexture(avgTex); gl = null;
    }

    unsafe void Upload()
    {
        List<(string key, List<MeshChunk>? chunks)> m; (byte[] px, int cols)? at; bool clr;
        lock (pending) { m = pending.ToList(); pending.Clear(); at = pendingAtlas; clr = clearMeshes; pendingAtlas = null; clearMeshes = false; }
        if (m.Count > 0 || clr || at != null) shadowDirty = true;
        void Drop(string k) { if (meshes.Remove(k, out var l)) foreach (var x in l) { gl!.DeleteVertexArray(x.vao); gl.DeleteBuffer(x.vbo); gl.DeleteBuffer(x.ebo); } }
        if (clr) foreach (var k in meshes.Keys.Where(k => k.StartsWith("r:")).ToList()) Drop(k);
        if (at is { } a)
        {
            int S = a.cols * Atlas.T; atlasCols = a.cols;
            gl!.BindTexture(TextureTarget.Texture2D, atlasTex);
            fixed (byte* p = a.px) gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, (uint)S, (uint)S, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            // verkleinerte Stufen (16, 8, 4, 2, 1 Pixel je Kachel) – jede Kachel für sich, mit Alpha gewichtet
            var lv = a.px; int ls = S;
            for (int level = 1; level <= 4; level++)
            {
                int ns = ls / 2; var nx = new byte[ns * ns * 4];
                for (int y = 0; y < ns; y++) for (int x = 0; x < ns; x++)
                {
                    int r = 0, g = 0, b = 0, al = 0;
                    for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                    { int o = (((y * 2 + dy) * ls) + x * 2 + dx) * 4; int aa = lv[o + 3]; r += lv[o] * aa; g += lv[o + 1] * aa; b += lv[o + 2] * aa; al += aa; }
                    int d = (y * ns + x) * 4;
                    if (al > 0) { nx[d] = (byte)(r / al); nx[d + 1] = (byte)(g / al); nx[d + 2] = (byte)(b / al); }
                    nx[d + 3] = (byte)Math.Min(255, al / 4 * 5 / 4);   // Laub & Co. dünnen in der Ferne nicht aus
                }
                fixed (byte* p = nx) gl.TexImage2D(TextureTarget.Texture2D, level, InternalFormat.Rgba, (uint)ns, (uint)ns, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                lv = nx; ls = ns;
            }
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 4);
            while (gl.GetError() != GLEnum.NoError) { }
            // keine anisotrope Filterung: unter Windows (Direct3D) macht sie auch nahe Texturen weich statt pixelscharf
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
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
            gl.EnableVertexAttribArray(4); gl.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, st, (void*)28);
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
    Vector3 Eye() => Fly || Walk ? eye : CamPos();
    Vector3 Forward() => Fly || Walk ? Dir(lookYaw, lookPitch) : Vector3.Normalize(target - CamPos());

    /* ---------- Laufen: wie im Spiel, mit Schwerkraft und Kollision ---------- */
    public bool Walk { get; private set; }
    public event Action<bool>? WalkChanged;
    float wx, wy, wz, vy; bool onGround;   // Füße in Projekt-Koordinaten, senkrechte Geschwindigkeit

    public void SetWalk(bool on)
    {
        var p = project; if (on == Walk || (on && p == null)) return;
        if (on)
        {
            var f = Forward(); var e0 = Eye();
            lookYaw = MathF.Atan2(f.Z, f.X); lookPitch = Math.Clamp(MathF.Asin(Math.Clamp(f.Y, -1, 1)), -1.2f, 1.2f);
            if (Fly) { Fly = false; FlyChanged?.Invoke(false); }
            // Einstieg: auf den Block, auf den die Kamera schaut; sonst senkrecht unter der Kamera
            var hit = CenterBlock();
            if (hit is { } h) { wx = h.x + .5f; wz = h.z + .5f; wy = Collision.GroundBelow(p!, wx, h.y + 1.5f, wz); }
            else { wx = Math.Clamp(e0.X + p!.W / 2f, 0, p.W); wz = Math.Clamp(e0.Z + p.D / 2f, 0, p.D); wy = Collision.GroundBelow(p, wx, Math.Min(e0.Y, p.H), wz); }
            for (int k = 0; k < 400 && !Collision.Free(p, wx - .3f, wx + .3f, wy, wy + 1.8f, wz - .3f, wz + .3f); k++) wy += .25f;   // nicht im Block stecken
            vy = 0; Walk = true; SyncEye();
            moveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => Step());
            lastTick = DateTime.Now; moveTimer.Start();
        }
        else
        {
            var f = Dir(lookYaw, lookPitch); target = eye + f * 6; dist = 6;
            yaw = MathF.Atan2(-f.Z, -f.X); pitch = Math.Clamp(MathF.Asin(Math.Clamp(-f.Y, -1, 1)), -1.5f, 1.5f);
            Walk = false;
        }
        WalkChanged?.Invoke(on); RequestNextFrameRendering();
    }

    void SyncEye() { var p = project!; eye = new Vector3(wx - p.W / 2f, wy + 1.62f, wz - p.D / 2f); }
    public string WalkPos => project == null ? "" : $"X {project.OriginX + (int)MathF.Floor(wx)}  Y {project.OriginY + (int)MathF.Floor(wy)}  Z {project.OriginZ + (int)MathF.Floor(wz)}";

    /// <summary>Block in der Bildmitte (Projekt-Koordinaten).</summary>
    (int x, int y, int z)? CenterBlock()
    {
        var p = project; if (p == null) return null;
        var o = Eye() + new Vector3(p.W / 2f, 0, p.D / 2f); var d = Forward();
        for (float t = 0; t < 2000; t += .2f)
        {
            var q = o + d * t; int x = (int)MathF.Floor(q.X), y = (int)MathF.Floor(q.Y), z = (int)MathF.Floor(q.Z);
            if (y < 0 && d.Y < 0) return null;
            if (y < p.H && y >= 0 && p.At(x, y, z) != 0 && Collision.Boxes(p.At(x, y, z)).Length > 0) return (x, y, z);
        }
        return null;
    }

    void WalkStep(float dt)
    {
        var p = project; if (p == null) { SetWalk(false); return; }
        var flat = new Vector3(MathF.Cos(lookYaw), 0, MathF.Sin(lookYaw)); var right = Vector3.Normalize(Vector3.Cross(flat, Vector3.UnitY));
        var mv = Vector3.Zero;
        bool fw = held.Contains(Key.W) || held.Contains(Key.Up);
        if (fw) mv += flat; if (held.Contains(Key.S) || held.Contains(Key.Down)) mv -= flat;
        if (held.Contains(Key.D) || held.Contains(Key.Right)) mv += right; if (held.Contains(Key.A) || held.Contains(Key.Left)) mv -= right;
        if (mv != Vector3.Zero) mv = Vector3.Normalize(mv);
        bool sprint = lastMods.HasFlag(KeyModifiers.Control), sneak = lastMods.HasFlag(KeyModifiers.Shift);
        float speed = sprint ? 5.6f : sneak ? 1.3f : 4.3f;
        int feet = p.At((int)MathF.Floor(wx), (int)MathF.Floor(wy + .1f), (int)MathF.Floor(wz)), body = p.At((int)MathF.Floor(wx), (int)MathF.Floor(wy + 1f), (int)MathF.Floor(wz));
        bool water = Collision.Liquid(feet) || Collision.Liquid(body), climb = Collision.Climbable(feet) || Collision.Climbable(body);
        bool jump = held.Contains(Key.Space), down = held.Contains(Key.C);
        if (water) { speed *= .5f; vy = jump ? 2.5f : Math.Max(vy - 9 * dt, -2.2f); }
        else if (climb) { vy = fw || jump ? 2.4f : down ? -2.4f : Math.Max(vy - 32 * dt, -1.5f); }
        else { if (jump && onGround) vy = 8.9f; vy = Math.Max(vy - 32 * dt, -60); }
        float dx = mv.X * speed * dt, dz = mv.Z * speed * dt, dy = vy * dt;
        int n = Math.Max(1, (int)MathF.Ceiling(Math.Max(Math.Abs(dy), Math.Max(Math.Abs(dx), Math.Abs(dz))) / .3f));
        bool grounded = false;
        for (int k = 0; k < n; k++)
        {
            float y0 = wy;
            var r = Collision.Move(p, wx, wy, wz, dx / n, dy / n, dz / n, onGround || grounded);
            wx = r.x; wz = r.z; wy = r.y;
            if (r.ground) { grounded = true; vy = Math.Max(vy, 0); if (vy > 0 && !jump) vy = 0; }
            if (dy > 0 && wy < y0 + dy / n - 1e-3f && !r.ground) vy = 0;   // Kopf angestoßen
        }
        onGround = grounded || !Collision.Free(p, wx - .3f, wx + .3f, wy - .06f, wy, wz - .3f, wz + .3f);
        if (onGround && vy < 0) vy = 0;
        SyncEye(); RequestNextFrameRendering(); WalkMoved?.Invoke();
    }
    public event Action? WalkMoved;

    public void SetFly(bool on)
    {
        if (on == Fly) return;
        if (on && Walk) SetWalk(false);
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
        float near = Fly || Walk ? 0.05f : Math.Max(0.05f, dist * 0.002f), far = Math.Max(5000, (Fly || Walk ? 0 : dist) * 6 + (project == null ? 0 : Math.Max(project.W, project.D) * 3));
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(70 * MathF.PI / 180, Math.Max(0.1f, aspect), near, far);
        return view * proj;
    }

    protected override unsafe void OnOpenGlRender(GlInterface gi, int fb)
    {
        if (gl == null) return;
        try { RenderFrame(fb); }
        catch (Exception e) { McAssets.Log("3D-Zeichnen: " + e); }
    }

    unsafe void RenderFrame(int fb)
    {
        Upload();
        double sc = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = Math.Max(1, (int)(Bounds.Width * sc)), h = Math.Max(1, (int)(Bounds.Height * sc));
        if (photoReq is { } pr) { photoReq = null; TakePhoto(pr); }
        var gl = this.gl!;
        // Kantenglättung: in eine mehrfach abgetastete Fläche zeichnen und geglättet ins Fenster übertragen
        if (msaaOk && EnsureMsaa(w, h))
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, msaaFbo);
            DrawScene(w, h, null, msaaFbo);
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, msaaFbo);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)fb);
            gl.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            if (gl.GetError() is var err && err != GLEnum.NoError) { msaaOk = false; McAssets.Log("Kantenglättung aus: " + err); }
            return;
        }
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        DrawScene(w, h, null, (uint)fb);
    }

    uint msaaFbo, msaaColor, msaaDepth; int msaaW, msaaH; bool msaaOk = true;

    /// <summary>Mehrfach abgetastete Zeichenfläche in Fenstergröße (4×, sonst so viel die Grafikkarte kann).</summary>
    bool EnsureMsaa(int w, int h)
    {
        var gl = this.gl!;
        if (msaaFbo != 0 && msaaW == w && msaaH == h) return true;
        try
        {
            if (msaaFbo != 0) { gl.DeleteFramebuffer(msaaFbo); gl.DeleteRenderbuffer(msaaColor); gl.DeleteRenderbuffer(msaaDepth); msaaFbo = 0; }
            while (gl.GetError() != GLEnum.NoError) { }
            gl.GetInteger((GetPName)0x8D57, out int maxS); int samples = Math.Min(4, maxS);   // 0x8D57 = höchste Abtastzahl
            if (samples < 2) { msaaOk = false; return false; }
            msaaColor = gl.GenRenderbuffer(); gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msaaColor);
            gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)samples, InternalFormat.Rgba8, (uint)w, (uint)h);
            msaaDepth = gl.GenRenderbuffer(); gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msaaDepth);
            gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)samples, InternalFormat.DepthComponent24, (uint)w, (uint)h);
            msaaFbo = gl.GenFramebuffer(); gl.BindFramebuffer(FramebufferTarget.Framebuffer, msaaFbo);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, msaaColor);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, msaaDepth);
            if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete || gl.GetError() != GLEnum.NoError) { msaaOk = false; return false; }
            msaaW = w; msaaH = h; return true;
        }
        catch (Exception e) { McAssets.Log("Kantenglättung nicht möglich: " + e.Message); msaaOk = false; return false; }
    }

    /// <summary>Szene zeichnen; ph = Einstellungen für ein Foto (sonst die normale Ansicht).</summary>
    unsafe void DrawScene(int w, int h, PhotoOptions? ph, uint targetFb)
    {
        var gl = this.gl!;
        bool shade = ShaderActive && project != null;
        Vector3 sunN = SunFromAngle(sunAngle);
        if (ph != null) sunN = ph.Time switch { PhotoTime.Noon => SunFromAngle(58), PhotoTime.Evening => SunFromAngle(168), _ => sunN };
        if (shade && meshes.Count > 0)
        {
            var p = project!; float r = 0.5f * MathF.Sqrt((float)p.W * p.W + (float)p.H * p.H + (float)p.D * p.D);
            gl.GetInteger(GetPName.MaxTextureSize, out int maxTex);
            int res = Math.Min(maxTex, r > 140 ? 4096 : 2048);
            if (shadowDirty || shadowSun != sunN || shadowSize != res) RenderShadow(sunN, res);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFb);
        }
        gl.Viewport(0, 0, (uint)w, (uint)h);
        bool nightNow = ph?.Time == PhotoTime.Night ? lightMap != null : ph == null || ph.Time == PhotoTime.AsView ? night && lightMap != null : false;
        var skyC = ph == null || ph.Time == PhotoTime.AsView ? SkyNow() : ph.Time switch { PhotoTime.Evening => new Vector3(0.93f, 0.62f, 0.45f), PhotoTime.Night => NightSky, _ => new Vector3(0.52f, 0.72f, 1.0f) };
        bool sOn = ph != null ? ph.Time is PhotoTime.Noon or PhotoTime.Evening : sunOn;
        var sDir = ph?.Time == PhotoTime.Evening ? new Vector3(-0.85f, 0.32f, 0.42f) : ph != null ? new Vector3(0.45f, 0.85f, 0.35f) : sunDir;
        var (horizon, zenith) = SkyColors(sunN, nightNow);
        if (shade) { skyC = horizon; sDir = sunN; sOn = false; }
        gl.ClearColor(skyC.X, skyC.Y, skyC.Z, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        gl.Disable(EnableCap.CullFace); gl.Disable(EnableCap.Blend);
        var mvp = ViewProj((float)w / h); var cam = Eye();
        if (shade)
        {   // Himmel mit Verlauf, Sonne und nachts Sternen
            gl.Disable(EnableCap.DepthTest); gl.DepthMask(false);
            gl.UseProgram(skyProg);
            Matrix4x4.Invert(mvp, out var inv);
            gl.UniformMatrix4(gl.GetUniformLocation(skyProg, "invVP"), 1, false, (float*)&inv);
            gl.Uniform3(gl.GetUniformLocation(skyProg, "sun"), sunN.X, sunN.Y, sunN.Z);
            gl.Uniform3(gl.GetUniformLocation(skyProg, "horizon"), horizon.X, horizon.Y, horizon.Z);
            gl.Uniform3(gl.GetUniformLocation(skyProg, "zenith"), zenith.X, zenith.Y, zenith.Z);
            gl.Uniform1(gl.GetUniformLocation(skyProg, "night"), nightNow ? 1f : 0f);
            gl.BindVertexArray(skyVao); gl.DrawArrays(PrimitiveType.Triangles, 0, 3); gl.BindVertexArray(0);
            gl.DepthMask(true);
        }
        gl.Enable(EnableCap.DepthTest); gl.DepthFunc(DepthFunction.Lequal);
        if (meshes.Count == 0) return;
        gl.UseProgram(prog);
        gl.Uniform1(uShaderOn, shade ? 1f : 0f);
        if (shade)
        {
            var lm = lightVP; gl.UniformMatrix4(uLmvp, 1, false, (float*)&lm);
            gl.ActiveTexture(TextureUnit.Texture2); gl.BindTexture(TextureTarget.Texture2D, shadowTex); gl.Uniform1(uShadowTex, 2); gl.ActiveTexture(TextureUnit.Texture0);
            gl.Uniform1(uShadowRes, (float)shadowSize); gl.Uniform1(uShadowTexel, shadowTexelSize); gl.Uniform1(uTime, (float)(Environment.TickCount64 % 3_600_000) / 1000f);
            gl.Uniform2(uViewport, (float)w, (float)h); gl.Uniform3(uZenith, zenith.X, zenith.Y, zenith.Z);
            var lm2 = lightMap; var pp = project!;
            if (lm2 != null && lm2 != lightUploaded) UploadLight(lm2, pp);
            gl.ActiveTexture(TextureUnit.Texture3); gl.BindTexture(TextureTarget.Texture3D, lightTex3); gl.Uniform1(uLightTex, 3); gl.ActiveTexture(TextureUnit.Texture0);
            gl.Uniform1(uLightOn, lightTexOk && lm2 != null && lm2 == lightUploaded ? 1f : 0f);
            gl.Uniform3(uLightSize, (float)pp.W, (float)pp.H, (float)pp.D); gl.Uniform3(uProjOff, pp.W / 2f, 0f, pp.D / 2f);
        }
        else { var id = Matrix4x4.Identity; gl.UniformMatrix4(uLmvp, 1, false, (float*)&id); gl.Uniform1(uShadowTexel, 0f); }
        gl.UniformMatrix4(uMvp, 1, false, (float*)&mvp);
        gl.Uniform1(uCols, (float)atlasCols); gl.Uniform3(uCam, cam.X, cam.Y, cam.Z); gl.Uniform3(uSky, skyC.X, skyC.Y, skyC.Z);
        gl.Uniform1(uNight, nightNow ? 1f : 0f); gl.Uniform1(uHiOn, hiY >= 0 && ph == null ? 1f : 0f); gl.Uniform1(uSunOn, sOn ? 1f : 0f); gl.Uniform3(uSun, sDir.X, sDir.Y, sDir.Z);
        float size = project == null ? 100 : Math.Max(project.W, project.D);
        float fd = Fly || Walk ? size * 0.5f : dist;
        if (ph != null && !ph.Fog) { gl.Uniform1(uFogNear, 1e6f); gl.Uniform1(uFogFar, 2e6f); }
        else { gl.Uniform1(uFogNear, fd * 1.2f + size * 0.6f); gl.Uniform1(uFogFar, fd * 3f + size * 1.5f + 200); }
        gl.ActiveTexture(TextureUnit.Texture0); gl.BindTexture(TextureTarget.Texture2D, atlasTex); gl.Uniform1(uAtlas, 0);
        gl.ActiveTexture(TextureUnit.Texture1); gl.BindTexture(TextureTarget.Texture2D, avgTex); gl.Uniform1(uAvg, 1); gl.ActiveTexture(TextureUnit.Texture0);
        bool Skip(string key) => ph != null && ((!ph.Guides && key is "guides") || key is "spawn" && !nightNow || (!ph.Players && key is "players"));
        // erst alles Feste, dann Wasser, Eis und farbiges Glas halb durchsichtig darüber
        gl.Uniform1(uTrans, 0f);
        foreach (var (k, l) in meshes) { if (Skip(k)) continue; foreach (var m in l) if (!m.trans) { gl.BindVertexArray(m.vao); gl.DrawElements(PrimitiveType.Triangles, (uint)m.count, DrawElementsType.UnsignedInt, (void*)0); } }
        gl.Enable(EnableCap.Blend); gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); gl.DepthMask(false);
        gl.Uniform1(uTrans, 1f);
        foreach (var (k, l) in meshes) { if (Skip(k)) continue; foreach (var m in l) if (m.trans) { gl.BindVertexArray(m.vao); gl.DrawElements(PrimitiveType.Triangles, (uint)m.count, DrawElementsType.UnsignedInt, (void*)0); } }
        gl.DepthMask(true); gl.Disable(EnableCap.Blend);
        gl.BindVertexArray(0);
    }

    /* ---------- Fotomodus: in eigene, große Bildfläche zeichnen und auslesen ---------- */
    public enum PhotoTime { AsView, Noon, Evening, Night }
    public sealed record PhotoOptions(int W, int H, PhotoTime Time, bool Fog, bool Guides, bool Players);
    (PhotoOptions opt, TaskCompletionSource<(byte[] rgba, int w, int h)?> done)? photoReq;

    /// <summary>Foto aufnehmen: RGBA-Pixel von oben nach unten, oder null, wenn die Grafikkarte das nicht kann.</summary>
    public Task<(byte[] rgba, int w, int h)?> CapturePhoto(PhotoOptions o)
    {
        var tcs = new TaskCompletionSource<(byte[], int, int)?>();
        photoReq = (o, tcs); RequestNextFrameRendering();
        return tcs.Task;
    }

    unsafe void TakePhoto((PhotoOptions opt, TaskCompletionSource<(byte[] rgba, int w, int h)?> done) req)
    {
        var gl = this.gl!; var o = req.opt;
        try
        {
            gl.GetInteger(GetPName.MaxRenderbufferSize, out int maxRb);
            int w = Math.Min(o.W, maxRb), h = Math.Min(o.H, maxRb);
            uint tex = gl.GenTexture(); gl.BindTexture(TextureTarget.Texture2D, tex);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            uint rb = gl.GenRenderbuffer(); gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rb);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)w, (uint)h);
            uint fbo = gl.GenFramebuffer(); gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, rb);
            if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete) throw new Exception("Bildfläche nicht möglich");
            // geglättet: erst mehrfach abgetastet zeichnen, dann ins Bild übertragen
            uint mf = 0, mc = 0, md = 0;
            if (msaaOk)
            {
                while (gl.GetError() != GLEnum.NoError) { }
                gl.GetInteger((GetPName)0x8D57, out int maxS); int smp = Math.Min(4, maxS);
                if (smp >= 2)
                {
                    mc = gl.GenRenderbuffer(); gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, mc); gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)smp, InternalFormat.Rgba8, (uint)w, (uint)h);
                    md = gl.GenRenderbuffer(); gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, md); gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)smp, InternalFormat.DepthComponent24, (uint)w, (uint)h);
                    mf = gl.GenFramebuffer(); gl.BindFramebuffer(FramebufferTarget.Framebuffer, mf);
                    gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, mc);
                    gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, md);
                    if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete || gl.GetError() != GLEnum.NoError)
                    { gl.DeleteFramebuffer(mf); gl.DeleteRenderbuffer(mc); gl.DeleteRenderbuffer(md); mf = 0; gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo); }
                }
            }
            if (mf != 0)
            {
                DrawScene(w, h, o, mf);
                gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, mf); gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
                gl.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                gl.DeleteFramebuffer(mf); gl.DeleteRenderbuffer(mc); gl.DeleteRenderbuffer(md);
            }
            else DrawScene(w, h, o, fbo);
            var px = new byte[(long)w * h * 4];
            fixed (byte* p = px) gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(fbo); gl.DeleteRenderbuffer(rb); gl.DeleteTexture(tex);
            // OpenGL liest von unten nach oben
            var flip = new byte[px.Length]; int row = w * 4;
            for (int y = 0; y < h; y++) System.Buffer.BlockCopy(px, (h - 1 - y) * row, flip, y * row, row);
            for (int i = 3; i < flip.Length; i += 4) flip[i] = 255;
            req.done.TrySetResult((flip, w, h));
        }
        catch (Exception e) { Console.Error.WriteLine("[3D] Foto: " + e.Message); req.done.TrySetResult(null); }
    }

    /* ---------- Maus ---------- */
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); Focus();
        var pp = e.GetCurrentPoint(this); dragFrom = pp.Position; pressAt = pp.Position; moved = false;
        panning = pp.Properties.IsRightButtonPressed || pp.Properties.IsMiddleButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        guideDrag = false;
        if (pp.Properties.IsLeftButtonPressed && !panning && !Fly && !Walk && GuideAt(pp.Position) is { } hit)
        {   // Hilfslinie anklicken = auswählen und mit der Maus verschieben
            Select(hit.g.Id); guideDrag = hit.g.Type != "outline"; grabY = hit.y; grabCell = PlaneCell(pp.Position, grabY);
        }
        e.Pointer.Capture(this); e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e); e.Pointer.Capture(null);
        if (guideDrag && moved && CurrentGuide() is { } g) GuideEdited?.Invoke(g, true);
        else if (!guideDrag && !moved && SelectedGuide != null && dragFrom != null && GuideAt(e.GetPosition(this)) == null) Select(null);   // Klick ins Leere: Auswahl aufheben
        guideDrag = false; dragFrom = null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (dragFrom != null && pressAt is { } pa && (Math.Abs(pos.X - pa.X) > 3 || Math.Abs(pos.Y - pa.Y) > 3)) moved = true;
        if (guideDrag && dragFrom != null)
        {
            if (PlaneCell(pos, grabY) is { } c && grabCell is { } c0 && c != c0 && CurrentGuide() is { } g)
            {
                int dx = c.x - c0.x, dz = c.z - c0.z; grabCell = c;
                g.Cx += dx; g.Cz += dz; g.X2 += dx; g.Z2 += dz;
                GuideEdited?.Invoke(g, false);
            }
            return;
        }
        if (dragFrom is { } from)
        {
            var d = pos - from; dragFrom = pos;
            if (Fly || Walk)
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
        if (Walk) { e.Handled = true; return; }
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
        if (CurrentGuide() is { } sg)
        {   // ausgewählte Hilfslinie: Bild↑/↓ = Höhe, +/− = Größe, Esc = fertig
            int dy = e.Key == Key.PageUp ? 1 : e.Key == Key.PageDown ? -1 : 0;
            int ds = e.Key is Key.OemPlus or Key.Add ? 1 : e.Key is Key.OemMinus or Key.Subtract ? -1 : 0;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { dy *= 5; ds *= 5; }
            if (dy != 0) { sg.Y += dy; if (sg.Type == "outline" && project != null) sg.ComputeOutline(project); GuideEdited?.Invoke(sg, true); e.Handled = true; return; }
            if (ds != 0 && sg.Type != "outline")
            {
                if (sg.Type == "line")
                {   // Linie vom Start aus verlängern/verkürzen
                    double lx = sg.X2 - sg.Cx, lz = sg.Z2 - sg.Cz, len = Math.Sqrt(lx * lx + lz * lz);
                    if (len > 0) { double nl = Math.Max(1, len + ds); sg.X2 = Math.Round(sg.Cx + lx / len * nl); sg.Z2 = Math.Round(sg.Cz + lz / len * nl); }
                }
                else if (sg.Type == "grid") sg.A = Math.Max(2, sg.A + ds);
                else { double k = (sg.A + 2 * ds) / Math.Max(1, sg.A); sg.A = Math.Max(1, sg.A + 2 * ds); sg.Bv = Math.Max(1, Math.Round(sg.Bv * k)); }
                GuideEdited?.Invoke(sg, true); e.Handled = true; return;
            }
            if (e.Key == Key.Escape) { Select(null); e.Handled = true; return; }
            if (e.Key == Key.Delete) { SelectedGuide = null; GuideDeleted?.Invoke(sg); GuideSelected?.Invoke(null); e.Handled = true; return; }
        }
        if (e.Key == Key.G) { SetWalk(!Walk); e.Handled = true; return; }
        if (e.Key == Key.F) { if (Walk) { var e0 = eye; var ly = lookYaw; var lp = lookPitch; SetWalk(false); eye = e0; SetFly(true); eye = e0; lookYaw = ly; lookPitch = lp; } else SetFly(!Fly); e.Handled = true; return; }
        if (e.Key == Key.Escape && Walk) { SetWalk(false); e.Handled = true; return; }
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
        if (Walk) { WalkStep(dt); return; }
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

    /* ---------- Hilfslinien in 3D bearbeiten ---------- */
    public string? SelectedGuide { get; private set; }
    /// <summary>Hilfslinie in 3D geändert; true = fertig (z. B. Maus losgelassen).</summary>
    public event Action<Guide, bool>? GuideEdited;
    public event Action<Guide?>? GuideSelected;
    public event Action<Guide>? GuideDeleted;
    bool guideDrag, moved; Point? pressAt; float grabY; (int x, int z)? grabCell;

    Guide? CurrentGuide() => SelectedGuide == null ? null : guidesPlan?.Guides.FirstOrDefault(g => g.Id == SelectedGuide);

    public void Select(string? id)
    {
        if (id == SelectedGuide) return;
        SelectedGuide = id; RebuildExtras(true); GuideSelected?.Invoke(CurrentGuide());
    }

    /// <summary>Strahl unter der Maus in Projekt-Koordinaten.</summary>
    (Vector3 o, Vector3 d)? Ray(Point pos)
    {
        var p = project; if (p == null || Bounds.Width < 1) return null;
        if (!Matrix4x4.Invert(ViewProj((float)(Bounds.Width / Bounds.Height)), out var inv)) return null;
        float nx = (float)(pos.X / Bounds.Width * 2 - 1), ny = (float)(1 - pos.Y / Bounds.Height * 2);
        var a = Vector4.Transform(new Vector4(nx, ny, -1, 1), inv); var b = Vector4.Transform(new Vector4(nx, ny, 1, 1), inv);
        var o = new Vector3(a.X, a.Y, a.Z) / a.W; var e2 = new Vector3(b.X, b.Y, b.Z) / b.W;
        return (o + new Vector3(p.W / 2f, 0, p.D / 2f), Vector3.Normalize(e2 - o));
    }

    /// <summary>Feld, in dem der Strahl die waagrechte Ebene auf Höhe y trifft.</summary>
    (int x, int z)? PlaneCell(Point pos, float y)
    {
        if (Ray(pos) is not { } r || Math.Abs(r.d.Y) < 1e-4f) return null;
        float t = (y - r.o.Y) / r.d.Y; if (t <= 0) return null;
        var q = r.o + r.d * t; return ((int)MathF.Floor(q.X), (int)MathF.Floor(q.Z));
    }

    /// <summary>Abstand zum ersten sichtbaren Block entlang des Strahls (Projekt-Koordinaten), höchstens bis lim.</summary>
    float? BlockHit(Vector3 o, Vector3 dv, float lim)
    {
        var p = project; if (p == null) return null;
        int maxY = Math.Min(top, p.H - 1); float t = 0, step = 0.1f;
        while (t < lim)
        {
            var q = o + dv * t; int x = (int)MathF.Floor(q.X), y = (int)MathF.Floor(q.Y), z = (int)MathF.Floor(q.Z);
            if (y >= 0 && y <= maxY && x >= 0 && z >= 0 && x < p.W && z < p.D && p.Cells[p.Index(x, y, z)] != 0) return t;
            t += step; if (t > 20) step = 0.25f;
        }
        return null;
    }

    /// <summary>Nächste Hilfslinie unter der Maus (Raster ausgenommen, das liegt überall).</summary>
    (Guide g, float y)? GuideAt(Point pos)
    {
        var p = project; if (p == null || !showGuides || guidesPlan == null || Ray(pos) is not { } r) return null;
        Guide? best = null; float bt = float.MaxValue, by = 0;
        var inv = new Vector3(1 / (r.d.X == 0 ? 1e-9f : r.d.X), 1 / (r.d.Y == 0 ? 1e-9f : r.d.Y), 1 / (r.d.Z == 0 ? 1e-9f : r.d.Z));
        foreach (var g in guidesPlan.Guides.Where(g => g.On && g.Type != "grid"))
        {
            float y0 = g.Y, y1 = g.Y + g.H;
            foreach (var (x, z) in g.Cells(p))
            {   // Strahl gegen Quader (Slab-Test), etwas größer zum leichteren Treffen
                float tx0 = (x - .15f - r.o.X) * inv.X, tx1 = (x + 1.15f - r.o.X) * inv.X;
                float ty0 = (y0 - r.o.Y) * inv.Y, ty1 = (y1 - r.o.Y) * inv.Y;
                float tz0 = (z - .15f - r.o.Z) * inv.Z, tz1 = (z + 1.15f - r.o.Z) * inv.Z;
                float tmin = Math.Max(Math.Max(Math.Min(tx0, tx1), Math.Min(ty0, ty1)), Math.Min(tz0, tz1));
                float tmax = Math.Min(Math.Min(Math.Max(tx0, tx1), Math.Max(ty0, ty1)), Math.Max(tz0, tz1));
                if (tmax >= Math.Max(tmin, 0) && tmin < bt) { bt = Math.Max(tmin, 0); best = g; by = Math.Clamp(r.o.Y + r.d.Y * bt, y0, y1); }
            }
        }
        if (best == null || BlockHit(r.o, r.d, bt) is { } tb && tb < bt - 0.5f) return null;   // Block davor: nicht anklickbar
        return (best, by);
    }

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
