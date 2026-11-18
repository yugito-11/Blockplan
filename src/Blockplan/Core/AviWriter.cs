using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Blockplan.Core;

/// <summary>Schreibt ein Video als AVI mit JPEG-Bildern (Motion-JPEG) – spielt in Windows-Fotos/Filme &amp; TV, VLC und Browsern ohne Zusatzprogramme.</summary>
public sealed class AviWriter : IDisposable
{
    readonly FileStream f; readonly BinaryWriter w; readonly int width, height, fps;
    readonly List<(int off, int len)> index = new();
    long riffSizePos, moviSizePos, moviStart, framesPos, streamFramesPos; int frames;

    public AviWriter(string path, int width, int height, int fps)
    {
        this.width = width; this.height = height; this.fps = Math.Max(1, fps);
        f = File.Create(path); w = new BinaryWriter(f, Encoding.ASCII);
        Fourcc("RIFF"); riffSizePos = f.Position; w.Write(0); Fourcc("AVI ");
        // Kopf
        Fourcc("LIST"); long hdrlSize = f.Position; w.Write(0); Fourcc("hdrl");
        Fourcc("avih"); w.Write(56);
        w.Write(1_000_000 / this.fps); w.Write(0); w.Write(0); w.Write(0x10);   // µs je Bild, max. Datenrate, Padding, Flags (hat Index)
        framesPos = f.Position; w.Write(0); w.Write(0); w.Write(1); w.Write(0); // Bilder, Startbilder, Spuren, Puffer
        w.Write(width); w.Write(height); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        Fourcc("LIST"); long strlSize = f.Position; w.Write(0); Fourcc("strl");
        Fourcc("strh"); w.Write(56); Fourcc("vids"); Fourcc("MJPG");
        w.Write(0); w.Write((short)0); w.Write((short)0); w.Write(0);   // Flags, Priorität, Sprache, Startbilder
        w.Write(1); w.Write(this.fps); w.Write(0); streamFramesPos = f.Position; w.Write(0);   // Skala, Rate, Start, Länge
        w.Write(0); w.Write(-1); w.Write(0); w.Write((short)0); w.Write((short)0); w.Write((short)width); w.Write((short)height);
        Fourcc("strf"); w.Write(40);
        w.Write(40); w.Write(width); w.Write(height); w.Write((short)1); w.Write((short)24); Fourcc("MJPG");
        w.Write(width * height * 3); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        Patch(strlSize); Patch(hdrlSize);
        Fourcc("LIST"); moviSizePos = f.Position; w.Write(0); moviStart = f.Position; Fourcc("movi");
    }

    void Fourcc(string s) => w.Write(Encoding.ASCII.GetBytes(s));
    void Patch(long sizePos) { long end = f.Position; f.Position = sizePos; w.Write((int)(end - sizePos - 4)); f.Position = end; }

    /// <summary>Ein Bild (JPEG-Daten) anhängen.</summary>
    public void AddFrame(byte[] jpeg)
    {
        int off = (int)(f.Position - moviStart);
        Fourcc("00dc"); w.Write(jpeg.Length); w.Write(jpeg); if ((jpeg.Length & 1) != 0) w.Write((byte)0);
        index.Add((off, jpeg.Length)); frames++;
    }

    public void Dispose()
    {
        Patch(moviSizePos);
        Fourcc("idx1"); w.Write(index.Count * 16);
        foreach (var (off, len) in index) { Fourcc("00dc"); w.Write(0x10); w.Write(off); w.Write(len); }
        Patch(riffSizePos);
        f.Position = framesPos; w.Write(frames); f.Position = streamFramesPos; w.Write(frames);
        w.Flush(); f.Dispose();
    }
}
