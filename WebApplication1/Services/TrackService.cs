using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        Console.WriteLine($"✅ Loaded {tracks.Count} tracks into cache");
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

        var page = filtered.Skip(offset).Take(limit).ToList();

        return Task.FromResult(page);
    }

    public async Task<Track> UploadTrackAsync(IFormFile audioFile, CreateTrackRequest? metadata, IFormFile? coverFile)
    {
        if (audioFile.Length > _maxFileSize)
            throw new InvalidOperationException($"File too large: {audioFile.Length} > {_maxFileSize}");

        var extension = Path.GetExtension(audioFile.FileName).ToLowerInvariant();
        var allowedExtensions = new[] { ".mp3", ".wav", ".flac", ".m4a", ".ogg" };

        if (!allowedExtensions.Contains(extension))
            throw new InvalidOperationException($"Unsupported format: {extension}");

        // Создаём уникальное имя файла
        var fileName = $"{Guid.NewGuid()}{extension}";
        var filePath = Path.Combine(_musicDir, fileName);

        // Сохраняем аудио файл
        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await audioFile.CopyToAsync(stream);
        }

        // Извлекаем метаданные
        var track = ExtractMetadata(filePath);

        // Применяем пользовательские метаданные
        if (metadata != null)
        {
            if (!string.IsNullOrWhiteSpace(metadata.ArtistName))
                track.ArtistName = metadata.ArtistName;
            if (!string.IsNullOrWhiteSpace(metadata.TrackTitle))
                track.TrackTitle = metadata.TrackTitle;
            if (!string.IsNullOrWhiteSpace(metadata.Album))
                track.Album = metadata.Album;
        }

        // Сохраняем обложку
        if (coverFile != null)
        {
            var coverFileName = $"{track.TrackId}.jpg";
            var coverPath = Path.Combine(_coversDir, coverFileName);

            using (var stream = new FileStream(coverPath, FileMode.Create))
            {
                await coverFile.CopyToAsync(stream);
            }

            track.CoverUrl = $"/covers/{coverFileName}";
        }

        // Сохраняем в БД
        _db.CreateTrack(track);
        _cache[track.TrackId] = track;

        Console.WriteLine($"🎵 Uploaded: {track.ArtistName} - {track.TrackTitle}");

        return track;
    }

    public async Task DeleteTrackAsync(string trackId)
    {
        var track = await GetTrackAsync(trackId);
        if (track == null)
            throw new FileNotFoundException("Track not found");

        // Удаляем из БД
        _db.DeleteTrack(trackId);

        // Удаляем из кэша
        _cache.TryRemove(trackId, out _);

        // Удаляем файлы
        if (File.Exists(track.FilePath))
            File.Delete(track.FilePath);

        if (!string.IsNullOrEmpty(track.CoverUrl))
        {
            var coverPath = Path.Combine(_coversDir, Path.GetFileName(track.CoverUrl));
            if (File.Exists(coverPath))
                File.Delete(coverPath);
        }

        Console.WriteLine($"🗑️ Deleted: {track.ArtistName} - {track.TrackTitle}");
    }

    public async Task<int> ScanDirectoryAsync()
    {
        if (!Directory.Exists(_musicDir))
            return 0;

        var allowedExtensions = new[] { ".mp3", ".wav", ".flac", ".m4a", ".ogg" };
        var files = Directory.GetFiles(_musicDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => allowedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        int indexed = 0;

        foreach (var file in files)
        {
            try
            {
                // Проверяем, не существует ли уже
                var existing = _db.GetAllTracks(10000, 0).FirstOrDefault(t => t.FilePath == file);
                if (existing != null)
                    continue;

                var track = ExtractMetadata(file);
                _db.CreateTrack(track);
                _cache[track.TrackId] = track;
                indexed++;

                Console.WriteLine($"📀 Indexed: {track.ArtistName} - {track.TrackTitle}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to index {file}: {ex.Message}");
            }
        }

        Console.WriteLine($"✅ Scan complete: {indexed} new tracks");
        return indexed;
    }

    public Task IncrementPlayCountAsync(string trackId)
    {
        _db.UpdatePlayCount(trackId);

        if (_cache.TryGetValue(trackId, out var track))
        {
            track.PlayCount++;
        }

        return Task.CompletedTask;
    }

    private Track ExtractMetadata(string filePath)
    {
        try
        {
            var file = TagLib.File.Create(filePath);
            var fileInfo = new FileInfo(filePath);

            var track = new Track
            {
                TrackId = GenerateTrackId(),
                ArtistName = file.Tag.FirstPerformer ?? "Unknown Artist",
                TrackTitle = file.Tag.Title ?? Path.GetFileNameWithoutExtension(filePath),
                Album = file.Tag.Album ?? "Unknown Album",
                Genre = file.Tag.FirstGenre,
                Year = (int)file.Tag.Year,
                DurationSeconds = (int)file.Properties.Duration.TotalSeconds,
                FilePath = filePath,
                FileSize = fileInfo.Length,
                Format = Path.GetExtension(filePath).TrimStart('.').ToUpper(),
                Bitrate = file.Properties.AudioBitrate,
                PlayCount = 0
            };

            // Извлекаем обложку из метаданных
            if (file.Tag.Pictures.Length > 0)
            {
                var picture = file.Tag.Pictures[0];
                var coverFileName = $"{track.TrackId}.jpg";
                var coverPath = Path.Combine(_coversDir, coverFileName);

                File.WriteAllBytes(coverPath, picture.Data.Data);
                track.CoverUrl = $"/covers/{coverFileName}";
            }

            return track;
        }
        catch (Exception ex)
        {
            // Fallback если TagLib не может прочитать файл
            var fileInfo = new FileInfo(filePath);

            return new Track
            {
                TrackId = GenerateTrackId(),
                ArtistName = "Unknown Artist",
                TrackTitle = Path.GetFileNameWithoutExtension(filePath),
                Album = "Unknown Album",
                DurationSeconds = 0,
                FilePath = filePath,
                FileSize = fileInfo.Length,
                Format = Path.GetExtension(filePath).TrimStart('.').ToUpper(),
                Bitrate = 0,
                PlayCount = 0
            };
        }
    }

    private string GenerateTrackId()
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}