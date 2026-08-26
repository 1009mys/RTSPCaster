using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using RTSPCaster.Models;

namespace RTSPCaster.Services;

public class SqliteService
{
    private readonly string _connectionString;

    public SqliteService(string? dbPath = null)
    {
        dbPath ??= Path.Combine(AppContext.BaseDirectory, "rtspcaster.db");
        _connectionString = $"Data Source={dbPath}";
        Initialize();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS VideoFiles (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    FilePath TEXT NOT NULL UNIQUE,
    FileName TEXT NOT NULL,
    FileSize INTEGER NOT NULL,
    VideoCodec TEXT,
    AudioCodec TEXT,
    DurationSeconds REAL NOT NULL,
    StreamCopyCompatible INTEGER NOT NULL,
    IncompatibleReason TEXT,
    CreatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS Channels (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    VideoFileId INTEGER NOT NULL,
    RtspPath TEXT NOT NULL,
    MediaMtxHost TEXT NOT NULL,
    MediaMtxPort INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS ConversionCache (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SourceVideoFileId INTEGER NOT NULL,
    SourcePath TEXT NOT NULL,
    ConvertedPath TEXT NOT NULL,
    SourceHash TEXT NOT NULL,
    ConvertedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS StreamHistory (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ChannelId INTEGER NOT NULL,
    StartedAt TEXT NOT NULL,
    EndedAt TEXT,
    Result TEXT NOT NULL,
    Message TEXT
);";
        cmd.ExecuteNonQuery();
    }

    public int UpsertVideoFile(VideoFile f)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO VideoFiles(FilePath,FileName,FileSize,VideoCodec,AudioCodec,DurationSeconds,StreamCopyCompatible,IncompatibleReason,CreatedAt)
VALUES ($p,$n,$s,$vc,$ac,$d,$sc,$ir,$c)
ON CONFLICT(FilePath) DO UPDATE SET
    FileName=excluded.FileName,
    FileSize=excluded.FileSize,
    VideoCodec=excluded.VideoCodec,
    AudioCodec=excluded.AudioCodec,
    DurationSeconds=excluded.DurationSeconds,
    StreamCopyCompatible=excluded.StreamCopyCompatible,
    IncompatibleReason=excluded.IncompatibleReason
RETURNING Id;";
        cmd.Parameters.AddWithValue("$p", f.FilePath);
        cmd.Parameters.AddWithValue("$n", f.FileName);
        cmd.Parameters.AddWithValue("$s", f.FileSize);
        cmd.Parameters.AddWithValue("$vc", (object?)f.VideoCodec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ac", (object?)f.AudioCodec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", f.DurationSeconds);
        cmd.Parameters.AddWithValue("$sc", f.StreamCopyCompatible ? 1 : 0);
        cmd.Parameters.AddWithValue("$ir", (object?)f.IncompatibleReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", f.CreatedAt.ToString("O"));
        f.Id = Convert.ToInt32(cmd.ExecuteScalar());
        return f.Id;
    }

    public int InsertChannel(Channel c)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO Channels(Name,VideoFileId,RtspPath,MediaMtxHost,MediaMtxPort,CreatedAt)
VALUES($n,$v,$r,$h,$p,$c) RETURNING Id;";
        cmd.Parameters.AddWithValue("$n", c.Name);
        cmd.Parameters.AddWithValue("$v", c.VideoFileId);
        cmd.Parameters.AddWithValue("$r", c.RtspPath);
        cmd.Parameters.AddWithValue("$h", c.MediaMtxHost);
        cmd.Parameters.AddWithValue("$p", c.MediaMtxPort);
        cmd.Parameters.AddWithValue("$c", c.CreatedAt.ToString("O"));
        c.Id = Convert.ToInt32(cmd.ExecuteScalar());
        return c.Id;
    }

    public void DeleteChannel(int id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Channels WHERE Id=$i;";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    }

    public void UpdateChannelRtspPath(int channelId, string rtspPath)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Channels SET RtspPath=$p WHERE Id=$i;";
        cmd.Parameters.AddWithValue("$p", rtspPath);
        cmd.Parameters.AddWithValue("$i", channelId);
        cmd.ExecuteNonQuery();
    }

    public List<(Channel Channel, VideoFile File)> LoadChannels()
    {
        var list = new List<(Channel, VideoFile)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT c.Id,c.Name,c.VideoFileId,c.RtspPath,c.MediaMtxHost,c.MediaMtxPort,c.CreatedAt,
    v.Id,v.FilePath,v.FileName,v.FileSize,v.VideoCodec,v.AudioCodec,v.DurationSeconds,v.StreamCopyCompatible,v.IncompatibleReason,v.CreatedAt
FROM Channels c JOIN VideoFiles v ON v.Id=c.VideoFileId;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var ch = new Channel
            {
                Id = r.GetInt32(0),
                Name = r.GetString(1),
                VideoFileId = r.GetInt32(2),
                RtspPath = r.GetString(3),
                MediaMtxHost = r.GetString(4),
                MediaMtxPort = r.GetInt32(5),
                CreatedAt = DateTime.Parse(r.GetString(6))
            };
            var vf = new VideoFile
            {
                Id = r.GetInt32(7),
                FilePath = r.GetString(8),
                FileName = r.GetString(9),
                FileSize = r.GetInt64(10),
                VideoCodec = r.IsDBNull(11) ? null : r.GetString(11),
                AudioCodec = r.IsDBNull(12) ? null : r.GetString(12),
                DurationSeconds = r.GetDouble(13),
                StreamCopyCompatible = r.GetInt32(14) == 1,
                IncompatibleReason = r.IsDBNull(15) ? null : r.GetString(15),
                CreatedAt = DateTime.Parse(r.GetString(16))
            };
            list.Add((ch, vf));
        }
        return list;
    }

    public ConversionCacheEntry? FindCache(int videoFileId, string hash)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id,SourceVideoFileId,SourcePath,ConvertedPath,SourceHash,ConvertedAt FROM ConversionCache WHERE SourceVideoFileId=$v AND SourceHash=$h LIMIT 1;";
        cmd.Parameters.AddWithValue("$v", videoFileId);
        cmd.Parameters.AddWithValue("$h", hash);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ConversionCacheEntry
        {
            Id = r.GetInt32(0),
            SourceVideoFileId = r.GetInt32(1),
            SourcePath = r.GetString(2),
            ConvertedPath = r.GetString(3),
            SourceHash = r.GetString(4),
            ConvertedAt = DateTime.Parse(r.GetString(5))
        };
    }

    public void SaveCache(ConversionCacheEntry e)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO ConversionCache(SourceVideoFileId,SourcePath,ConvertedPath,SourceHash,ConvertedAt)
VALUES($v,$s,$c,$h,$t);";
        cmd.Parameters.AddWithValue("$v", e.SourceVideoFileId);
        cmd.Parameters.AddWithValue("$s", e.SourcePath);
        cmd.Parameters.AddWithValue("$c", e.ConvertedPath);
        cmd.Parameters.AddWithValue("$h", e.SourceHash);
        cmd.Parameters.AddWithValue("$t", e.ConvertedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public int StartHistory(int channelId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO StreamHistory(ChannelId,StartedAt,Result) VALUES($c,$s,'started') RETURNING Id;";
        cmd.Parameters.AddWithValue("$c", channelId);
        cmd.Parameters.AddWithValue("$s", DateTime.UtcNow.ToString("O"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void EndHistory(int historyId, string result, string? message)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE StreamHistory SET EndedAt=$e,Result=$r,Message=$m WHERE Id=$i;";
        cmd.Parameters.AddWithValue("$e", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$r", result);
        cmd.Parameters.AddWithValue("$m", (object?)message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$i", historyId);
        cmd.ExecuteNonQuery();
    }
}
