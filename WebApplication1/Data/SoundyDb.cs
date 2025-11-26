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
        const string sql = "SELECT * FROM tracks WHERE track_id = @track_id LIMIT 1";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@track_id", trackId);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return ReadTrack(reader);
        }

        return null;
    }

    public List<Track> GetAllTracks(int limit, int offset)
    {
        const string sql = "SELECT * FROM tracks ORDER BY added_at DESC LIMIT @limit OFFSET @offset";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        var tracks = new List<Track>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            tracks.Add(ReadTrack(reader));
        }

        return tracks;
    }

    public void DeleteTrack(string trackId)
    {
        const string sql = "DELETE FROM tracks WHERE track_id = @track_id";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@track_id", trackId);
        cmd.ExecuteNonQuery();
    }

    public void UpdatePlayCount(string trackId)
    {
        const string sql = "UPDATE tracks SET play_count = play_count + 1, updated_at = CURRENT_TIMESTAMP WHERE track_id = @track_id";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@track_id", trackId);
        cmd.ExecuteNonQuery();
    }

    private Track ReadTrack(IDataReader reader)
    {
        return new Track
        {
            TrackId = reader.GetString(reader.GetOrdinal("track_id")),
            ArtistName = reader.GetString(reader.GetOrdinal("artist_name")),
            TrackTitle = reader.GetString(reader.GetOrdinal("track_title")),
            Album = reader.IsDBNull(reader.GetOrdinal("album")) ? null : reader.GetString(reader.GetOrdinal("album")),
            Genre = reader.IsDBNull(reader.GetOrdinal("genre")) ? null : reader.GetString(reader.GetOrdinal("genre")),
            Year = reader.GetInt32(reader.GetOrdinal("year")),
            DurationSeconds = reader.GetInt32(reader.GetOrdinal("duration_seconds")),
            FilePath = reader.GetString(reader.GetOrdinal("file_path")),
            CoverUrl = reader.IsDBNull(reader.GetOrdinal("cover_url")) ? null : reader.GetString(reader.GetOrdinal("cover_url")),
            FileSize = reader.GetInt64(reader.GetOrdinal("file_size")),
            Format = reader.GetString(reader.GetOrdinal("format")),
            Bitrate = reader.GetInt32(reader.GetOrdinal("bitrate")),
            PlayCount = reader.GetInt32(reader.GetOrdinal("play_count"))
        };
    }

    // ------- Users -------

    public void CreateUser(string username, string passwordHash)
    {
        const string sql = @"
            INSERT OR IGNORE INTO users (username, password_hash)
            VALUES (@username, @password_hash)";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@username", username);
        cmd.Parameters.AddWithValue("@password_hash", passwordHash);
        cmd.ExecuteNonQuery();
    }

    public User? GetUserByUsername(string username)
    {
        const string sql = "SELECT * FROM users WHERE username = @username LIMIT 1";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@username", username);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new User
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                Username = reader.GetString(reader.GetOrdinal("username")),
                PasswordHash = reader.GetString(reader.GetOrdinal("password_hash"))
            };
        }

        return null;
    }
}