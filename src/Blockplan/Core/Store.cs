using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Blockplan.Core;

public sealed record ProjectInfo(string Id, string Name, long Updated, int W, int D, int H)
{
    public override string ToString() => Name;
}

/// <summary>Eigene Datenbank: eine SQLite-Datei unter %LOCALAPPDATA%\Blockplan\blockplan.db.</summary>
public sealed class Store : IDisposable
{
    public static string Dir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blockplan");
            Directory.CreateDirectory(d); return d;
        }
    }

    readonly SqliteConnection db;
    public string File { get; }

    public Store(string? path = null)
    {
        File = path ?? Path.Combine(Dir, "blockplan.db");
        db = new SqliteConnection("Data Source=" + File);
        db.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("CREATE TABLE IF NOT EXISTS projects(id TEXT PRIMARY KEY, name TEXT NOT NULL, updated INTEGER NOT NULL, w INTEGER, d INTEGER, h INTEGER, data BLOB NOT NULL)");
        Exec("CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT)");
        Exec("CREATE TABLE IF NOT EXISTS timelapse(pid TEXT NOT NULL, t INTEGER NOT NULL, data BLOB NOT NULL)");
        Exec("CREATE INDEX IF NOT EXISTS timelapse_pid ON timelapse(pid, t)");
    }

    readonly object gate = new();

    /* ---------- Zeitraffer: Bauverlauf je Projekt ---------- */
    public void AddTimelapse(string pid, long t, byte[] data)
    {
        lock (gate)
        {
            using var c = db.CreateCommand();
            c.CommandText = "INSERT INTO timelapse(pid,t,data) VALUES($p,$t,$d)";
            c.Parameters.AddWithValue("$p", pid); c.Parameters.AddWithValue("$t", t); c.Parameters.AddWithValue("$d", data); c.ExecuteNonQuery();
        }
    }

    public List<byte[]> LoadTimelapse(string pid)
    {
        lock (gate)
        {
            var r = new List<byte[]>();
            using var c = db.CreateCommand();
            c.CommandText = "SELECT data FROM timelapse WHERE pid=$p ORDER BY t"; c.Parameters.AddWithValue("$p", pid);
            using var rd = c.ExecuteReader(); while (rd.Read()) r.Add((byte[])rd[0]);
            return r;
        }
    }

    public void DeleteTimelapse(string pid)
    {
        lock (gate) { using var c = db.CreateCommand(); c.CommandText = "DELETE FROM timelapse WHERE pid=$p"; c.Parameters.AddWithValue("$p", pid); c.ExecuteNonQuery(); }
    }

    void Exec(string sql) { using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }

    public List<ProjectInfo> List()
    {
        lock (gate)
        {
            var r = new List<ProjectInfo>();
            using var c = db.CreateCommand();
            c.CommandText = "SELECT id,name,updated,w,d,h FROM projects ORDER BY updated DESC";
            using var rd = c.ExecuteReader();
            while (rd.Read()) r.Add(new ProjectInfo(rd.GetString(0), rd.GetString(1), rd.GetInt64(2), rd.GetInt32(3), rd.GetInt32(4), rd.GetInt32(5)));
            return r;
            }
    }

    public void Save(Project p)
    {
        lock (gate)
        {
            p.Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var json = Encoding.UTF8.GetBytes(p.ToJson());
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) gz.Write(json);
            using var c = db.CreateCommand();
            c.CommandText = "INSERT INTO projects(id,name,updated,w,d,h,data) VALUES($i,$n,$u,$w,$d,$h,$b) " +
                            "ON CONFLICT(id) DO UPDATE SET name=$n,updated=$u,w=$w,d=$d,h=$h,data=$b";
            c.Parameters.AddWithValue("$i", p.Id); c.Parameters.AddWithValue("$n", p.Name); c.Parameters.AddWithValue("$u", p.Updated);
            c.Parameters.AddWithValue("$w", p.W); c.Parameters.AddWithValue("$d", p.D); c.Parameters.AddWithValue("$h", p.H);
            c.Parameters.AddWithValue("$b", ms.ToArray());
            c.ExecuteNonQuery();
            }
    }

    public Project? Load(string id)
    {
        lock (gate)
        {
            using var c = db.CreateCommand();
            c.CommandText = "SELECT data FROM projects WHERE id=$i"; c.Parameters.AddWithValue("$i", id);
            if (c.ExecuteScalar() is not byte[] b) return null;
            using var gz = new GZipStream(new MemoryStream(b), CompressionMode.Decompress);
            using var sr = new StreamReader(gz, Encoding.UTF8);
            return Project.FromJson(sr.ReadToEnd());
            }
    }

    public void Delete(string id)
    {
        lock (gate)
        {
            using var c = db.CreateCommand();
            c.CommandText = "DELETE FROM projects WHERE id=$i"; c.Parameters.AddWithValue("$i", id); c.ExecuteNonQuery();
            }
    }

    public string? Get(string key)
    {
        lock (gate)
        {
            using var c = db.CreateCommand();
            c.CommandText = "SELECT value FROM settings WHERE key=$k"; c.Parameters.AddWithValue("$k", key);
            return c.ExecuteScalar() as string;
            }
    }

    public void Set(string key, string? value)
    {
        lock (gate)
        {
            using var c = db.CreateCommand();
            c.CommandText = "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
            c.Parameters.AddWithValue("$k", key); c.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value); c.ExecuteNonQuery();
            }
    }

    public void Dispose() => db.Dispose();
}
