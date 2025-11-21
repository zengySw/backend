using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Soundy.Backend.Data;
using Soundy.Backend.Models;

namespace Soundy.Backend.Services;

public class TrackService : ITrackService
{
    private readonly SoundyDb _db;
    private readonly string _musicDir;
    private readonly string _coversDir;
    private readonly long _maxFileSize;
    private readonly ConcurrentDictionary<string, Track> _cache = new();

    public TrackService(SoundyDb db, string musicDir, string coversDir, long maxFileSize)
    {
        _db = db;
        _musicDir = musicDir;
        _coversDir = coversDir;
        _maxFileSize = maxFileSize;
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_musicDir);
        Directory.CreateDirectory(_coversDir);

        var tracks = _db.GetAllTracks(10000, 0);
        foreach (var t in tracks)
        {
            _cache[t.TrackId] = t;
        }

        Console.WriteLine($"Loaded {tracks.Count} tracks into cache");
        return Task.CompletedTask;
    }

    public Task<Track?> GetTrackAsync(string trackId)
    {
        if (_cache.TryGetValue(trackId, out var cached))
            return Task.FromResult<Track?>(cached);

        var track = _db.GetTrack(trackId);
        if (track != null)
        {
            _cache[track.TrackId] = track;
        }

        return Task.FromResult<Track?>(track);
    }

    public Task<List<Track>> GetAllTracksAsync(int limit, int offset)
    {
        var tracks = _db.GetAllTracks(limit, offset);
        return Task.FromResult(tracks);
    }

    public Task<List<Track>> SearchTracksAsync(string? query, int limit, int offset)
    {
        if (string.IsNullOrWhiteSpace(query))
            return GetAllTracksAsync(limit, offset);

        var lower = query.ToLowerInvariant();
        var all = _db.GetAllTracks(10000, 0);

        var filtered = all.FindAll(t =>
            (t.ArtistName ?? "").ToLowerInvariant().Contains(lower) ||
            (t.TrackTitle ?? "").ToLowerInvariant().Contains(lower) ||
            (t.Album ?? "").ToLowerInvariant().Contains(lower));

        var page = filtered.GetRange(
            Math.Min(offset, filtered.Count),
            Math.Max(0, Math.Min(limit, filtered.Count - offset))
        );

        return Task.FromResult(page);
    }

    public async Task<Track> UploadTrackAsync(IFormFile audioFile, CreateTrackRequest? metadata, IFormFile? coverFile)
    {
        var ext = Path.GetExtension(audioFile.FileName).ToLowerInvariant();
        if (!IsValidAudioFormat(ext))
            throw new InvalidOperationException($"Unsupported audio format: {ext}");

        if (audioFile.Length > _maxFileSize)
            throw new InvalidOperationException($"File too large (max {_maxFileSize / (1024 * 1024)} MB)");

        var tmpPath = Path.Combine(_musicDir, $"tmp_{Guid.NewGuid()}{ext}");
        await using (var fs = File.Create(tmpPath))
        {
            await audioFile.CopyToAsync(fs);
        }

        try
        {
            var track = ExtractMetadata(tmpPath);

            if (metadata != null)
            {
                if (!string.IsNullOrWhiteSpace(metadata.ArtistName))
                    track.ArtistName = metadata.ArtistName!;
                if (!string.IsNullOrWhiteSpace(metadata.TrackTitle))
                    track.TrackTitle = metadata.TrackTitle!;
                if (!string.IsNullOrWhiteSpace(metadata.Album))
                    track.Album = metadata.Album!;
            }

            track.TrackId = GenerateTrackId(track.ArtistName, track.TrackTitle, track.DurationSeconds);
            track.Format = ext.TrimStart('.');
            track.FileSize = new FileInfo(tmpPath).Length;

            if (_db.TrackExists(track.TrackId))
            {
                File.Delete(tmpPath);
                throw new InvalidOperationException("Track already exists");
            }

            var finalPath = Path.Combine(_musicDir, track.TrackId + ext);
            File.Move(tmpPath, finalPath);
            track.FilePath = finalPath;

            if (coverFile != null)
            {
                var coverPath = Path.Combine(_coversDir, track.TrackId + ".jpg");
                await using var coverStream = File.Create(coverPath);
                await coverFile.CopyToAsync(coverStream);
                track.CoverUrl = $"/covers/{track.TrackId}.jpg";
            }

            track.AddedAt = DateTime.UtcNow;
            track.UpdatedAt = track.AddedAt;

            _db.CreateTrack(track);
            _cache[track.TrackId] = track;

            return track;
        }
        catch
        {
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
            throw;
        }
    }

    public async Task DeleteTrackAsync(string trackId)
    {
        var track = await GetTrackAsync(trackId);
        if (track == null)
            throw new InvalidOperationException("Track not found");

        _db.DeleteTrack(trackId);

        if (File.Exists(track.FilePath))
            File.Delete(track.FilePath);

        if (!string.IsNullOrEmpty(track.CoverUrl))
        {
            var coverPath = Path.Combine(".", track.CoverUrl.TrimStart('/').Replace("/", Path.DirectorySeparatorChar.ToString()));
            if (File.Exists(coverPath))
                File.Delete(coverPath);
        }

        _cache.TryRemove(trackId, out _);
    }

    public Task<int> ScanDirectoryAsync()
    {
        var files = Directory.GetFiles(_musicDir);
        var indexed = 0;

        foreach (var path in files)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!IsValidAudioFormat(ext)) continue;

            var track = ExtractMetadata(path);
            track.TrackId = GenerateTrackId(track.ArtistName, track.TrackTitle, track.DurationSeconds);
            track.FilePath = path;
            track.Format = ext.TrimStart('.');
            track.FileSize = new FileInfo(path).Length;
            track.AddedAt = DateTime.UtcNow;
            track.UpdatedAt = track.AddedAt;

            if (_db.TrackExists(track.TrackId))
                continue;

            _db.CreateTrack(track);
            _cache[track.TrackId] = track;
            indexed++;
        }

        return Task.FromResult(indexed);
    }

    public Task IncrementPlayCountAsync(string trackId)
    {
        _db.IncrementPlayCount(trackId);

        if (_cache.TryGetValue(trackId, out var t))
        {
            t.PlayCount++;
            _cache[trackId] = t;
        }

        return Task.CompletedTask;
    }

    private Track ExtractMetadata(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);

        string artist = "Unknown Artist";
        string title = fileName;
        string album = "Unknown Album";

        if (fileName.Contains('-'))
        {
            var parts = fileName.Split('-', 2);
            artist = parts[0].Trim();
            title = parts[1].Trim();
        }

        return new Track
        {
            ArtistName = artist,
            TrackTitle = title,
            Album = album,
            Genre = "",
            Year = 0,
            DurationSeconds = 0
        };
    }

    private static bool IsValidAudioFormat(string ext)
    {
        var valid = new[] { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac" };
        foreach (var v in valid)
            if (ext == v) return true;
        return false;
    }

    private static string GenerateTrackId(string artist, string title, int duration)
    {
        var data = $"{artist}|{title}|{duration}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(data));
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()[..16];
    }
}
