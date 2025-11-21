using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Microsoft.Data.Sqlite;
using Soundy.Backend.Models;

namespace Soundy.Backend.Data;

public class SoundyDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public SoundyDb(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        Migrate();
    }

    private void Migrate()
    {
        var schema = @"
CREATE TABLE IF NOT EXISTS tracks (
    track_id TEXT PRIMARY KEY,
    artist_name TEXT NOT NULL,
    track_title TEXT NOT NULL,
    album TEXT,
    genre TEXT,
    year INTEGER,
    duration_seconds INTEGER,
    file_path TEXT NOT NULL UNIQUE,
    cover_url TEXT,
    file_size INTEGER,
    format TEXT,
    bitrate INTEGER,
    play_count INTEGER DEFAULT 0,
    added_at TEXT DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_artist_name ON tracks(artist_name);
CREATE INDEX IF NOT EXISTS idx_track_title ON tracks(track_title);
CREATE INDEX IF NOT EXISTS idx_album ON tracks(album);
CREATE INDEX IF NOT EXISTS idx_genre ON tracks(genre);
CREATE INDEX IF NOT EXISTS idx_added_at ON tracks(added_at DESC);

CREATE TABLE IF NOT EXISTS users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    username TEXT NOT NULL UNIQUE,
    password_hash TEXT NOT NULL,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP
);
";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    // ------- Tracks -------

    public int CreateTrack(Track track)
    {
        const string sql = @"
INSERT INTO tracks (
    track_id, artist_name, track_title, album, genre, year,
    duration_seconds, file_path, cover_url, file_size, format, bitrate
) VALUES (
    @track_id, @artist_name, @track_title, @album, @genre, @year,
    @duration_seconds, @file_path, @cover_url, @file_size, @format, @bitrate
);";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@track_id", track.TrackId);
        cmd.Parameters.AddWithValue("@artist_name", track.ArtistName);
        cmd.Parameters.AddWithValue("@track_title", track.TrackTitle);
        cmd.Parameters.AddWithValue("@album", (object?)track.Album ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@genre", (object?)track.Genre ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@year", track.Year);
        cmd.Parameters.AddWithValue("@duration_seconds", track.DurationSeconds);
        cmd.Parameters.AddWithValue("@file_path", track.FilePath);
        cmd.Parameters.AddWithValue("@cover_url", (object?)track.CoverUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@file_size", track.FileSize);
        cmd.Parameters.AddWithValue("@format", track.Format);
        cmd.Parameters.AddWithValue("@bitrate", track.Bitrate);

        return cmd.ExecuteNonQuery();
    }

    public Track? GetTrack(string trackId)
    {
        const string sql = @"
SELECT track_id, artist_name, track_title, album, genre, year,
       duration_seconds, file_path, cover_url, file_size, format, bitrate,
       play_count, added_at, updated_at
FROM tracks WHERE track_id = @track_id;
";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@track_id", trackId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return MapTrack(reader);
    }

    public List<Track> GetAllTracks(int limit, int offset)
    {
        const string sql = @"
SELECT track_id, artist_name, track_title, album, genre, year,
       duration_seconds, file_path, cover_url, file_size, format, bitrate,
       play_count, added_at, updated_at
FROM tracks
ORDER BY datetime(added_at) DESC
LIMIT @limit OFFSET @offset;
";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        using var reader = cmd.ExecuteReader();
        var list = new List<Track>();

        while (reader.Read())
        {
            list.Add(MapTrack(reader));
        }

        return list;
    }

    public bool TrackExists(string trackId)
    {
        const string sql = "SELECT EXISTS(SELECT 1 FROM tracks WHERE track_id = @id);";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", trackId);
        var result = cmd.ExecuteScalar();
        return Convert.ToInt32(result) == 1;
    }

    public int DeleteTrack(string trackId)
    {
        const string sql = "DELETE FROM tracks WHERE track_id = @id;";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", trackId);
        return cmd.ExecuteNonQuery();
    }

    public void IncrementPlayCount(string trackId)
    {
        const string sql = "UPDATE tracks SET play_count = play_count + 1 WHERE track_id = @id;";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", trackId);
        cmd.ExecuteNonQuery();
    }

    private static Track MapTrack(IDataRecord r)
    {
        return new Track
        {
            TrackId = r.GetString(0),
            ArtistName = r.GetString(1),
            TrackTitle = r.GetString(2),
            Album = r.IsDBNull(3) ? "" : r.GetString(3),
            Genre = r.IsDBNull(4) ? "" : r.GetString(4),
            Year = r.IsDBNull(5) ? 0 : r.GetInt32(5),
            DurationSeconds = r.IsDBNull(6) ? 0 : r.GetInt32(6),
            FilePath = r.GetString(7),
            CoverUrl = r.IsDBNull(8) ? null : r.GetString(8),
            FileSize = r.IsDBNull(9) ? 0L : r.GetInt64(9),
            Format = r.IsDBNull(10) ? "" : r.GetString(10),
            Bitrate = r.IsDBNull(11) ? 0 : r.GetInt32(11),
            PlayCount = r.IsDBNull(12) ? 0 : r.GetInt32(12),
            AddedAt = DateTime.TryParse(r.GetString(13), out var a) ? a : DateTime.UtcNow,
            UpdatedAt = DateTime.TryParse(r.GetString(14), out var u) ? u : DateTime.UtcNow
        };
    }

    // ------- Users -------

    public void CreateUser(string username, string passwordHash)
    {
        const string sql = "INSERT INTO users (username, password_hash) VALUES (@u, @p);";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@u", username);
        cmd.Parameters.AddWithValue("@p", passwordHash);

        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // скорей всего уже есть — как в Go-коде, просто игнорим ошибку
        }
    }

    public User? GetUserByUsername(string username)
    {
        const string sql = "SELECT id, username, password_hash, created_at FROM users WHERE username = @u;";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@u", username);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new User
        {
            Id = reader.GetInt32(0),
            Username = reader.GetString(1),
            PasswordHash = reader.GetString(2),
            CreatedAt = DateTime.TryParse(reader.GetString(3), out var c) ? c : DateTime.UtcNow
        };
    }
}
