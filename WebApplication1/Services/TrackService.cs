using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Soundy.Backend.Data;
using Soundy.Backend.Models;

namespace Soundy.Backend.Services;

public class TrackService : ITrackService
{
    private readonly SoundyDb _db;
    private readonly string _musicDir;
    private readonly string _coversDir;
    private readonly long _maxFileSize;
    private readonly ILogger<TrackService> _logger;
    private readonly ConcurrentDictionary<string, Track> _cache = new();

    public TrackService(
        SoundyDb db,
        string musicDir,
        string coversDir,
        long maxFileSize,
        ILogger<TrackService> logger)
    {
        _db = db;
        _musicDir = musicDir;
        _coversDir = coversDir;
        _maxFileSize = maxFileSize;
        _logger = logger;
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

        _logger.LogInformation("Loaded {Count} tracks into cache", tracks.Count);
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
            _logger.LogDebug("Track {TrackId} loaded from database into cache", trackId[..8]);
        }

        return Task.FromResult<Track?>(track);
    }

    public Task<List<Track>> GetAllTracksAsync(int limit, int offset)
    {
        var tracks = _db.GetAllTracks(limit, offset);
        _logger.LogDebug("Retrieved {Count} tracks (limit: {Limit}, offset: {Offset})", tracks.Count, limit, offset);
        return Task.FromResult(tracks);
    }

    public Task<int> GetTotalTracksCountAsync()
    {
        var count = _db.GetTotalTracksCount();
        return Task.FromResult(count);
    }

    public Task<List<Track>> SearchTracksAsync(string? query, int limit, int offset)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogDebug("Empty search query, returning all tracks");
            return GetAllTracksAsync(limit, offset);
        }

        var lower = query.ToLowerInvariant();

        // ✅ ОПТИМИЗАЦИЯ: Ищем в кэше вместо загрузки из БД
        var filtered = _cache.Values
            .Where(t =>
                (t.ArtistName ?? "").ToLowerInvariant().Contains(lower) ||
                (t.TrackTitle ?? "").ToLowerInvariant().Contains(lower) ||
                (t.Album ?? "").ToLowerInvariant().Contains(lower))
            .Skip(offset)
            .Take(limit)
            .ToList();

        _logger.LogInformation(
            "Search for '{Query}' returned {Count} results",
            query,
            filtered.Count
        );

        return Task.FromResult(filtered);
    }

    public async Task<Track> UploadTrackAsync(IFormFile audioFile, CreateTrackRequest? metadata, IFormFile? coverFile)
    {
        if (audioFile.Length > _maxFileSize)
        {
            _logger.LogWarning(
                "Upload rejected: file size {Size} exceeds maximum {MaxSize}",
                audioFile.Length,
                _maxFileSize
            );
            throw new InvalidOperationException($"File too large: {audioFile.Length} > {_maxFileSize}");
        }

        var extension = Path.GetExtension(audioFile.FileName).ToLowerInvariant();
        var allowedExtensions = new[] { ".mp3", ".wav", ".flac", ".m4a", ".ogg" };

        if (!allowedExtensions.Contains(extension))
        {
            _logger.LogWarning("Upload rejected: unsupported format {Extension}", extension);
            throw new InvalidOperationException($"Unsupported format: {extension}");
        }

        // Создаём уникальное имя файла
        var fileName = $"{Guid.NewGuid()}{extension}";
        var filePath = Path.Combine(_musicDir, fileName);

        _logger.LogDebug("Saving audio file to {FilePath}", filePath);

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

            _logger.LogDebug("Saving cover image to {CoverPath}", coverPath);

            using (var stream = new FileStream(coverPath, FileMode.Create))
            {
                await coverFile.CopyToAsync(stream);
            }

            track.CoverUrl = $"/covers/{coverFileName}";
        }

        // Сохраняем в БД
        _db.CreateTrack(track);
        _cache[track.TrackId] = track;

        _logger.LogInformation(
            "Uploaded track: {Artist} - {Title} (ID: {TrackId}, Size: {Size} bytes)",
            track.ArtistName,
            track.TrackTitle,
            track.TrackId[..8],
            track.FileSize
        );

        return track;
    }

    public async Task DeleteTrackAsync(string trackId)
    {
        var track = await GetTrackAsync(trackId);
        if (track == null)
        {
            _logger.LogWarning("Delete failed: track {TrackId} not found", trackId[..8]);
            throw new FileNotFoundException("Track not found");
        }

        try
        {
            // Удаляем из БД
            _db.DeleteTrack(trackId);

            // Удаляем из кэша
            _cache.TryRemove(trackId, out _);

            // Удаляем файлы
            if (File.Exists(track.FilePath))
            {
                File.Delete(track.FilePath);
                _logger.LogDebug("Deleted audio file: {FilePath}", track.FilePath);
            }

            if (!string.IsNullOrEmpty(track.CoverUrl))
            {
                var coverPath = Path.Combine(_coversDir, Path.GetFileName(track.CoverUrl));
                if (File.Exists(coverPath))
                {
                    File.Delete(coverPath);
                    _logger.LogDebug("Deleted cover image: {CoverPath}", coverPath);
                }
            }

            _logger.LogInformation(
                "Deleted track: {Artist} - {Title} (ID: {TrackId})",
                track.ArtistName,
                track.TrackTitle,
                trackId[..8]
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting track {TrackId}", trackId[..8]);
            throw;
        }
    }

    public async Task<int> ScanDirectoryAsync()
    {
        if (!Directory.Exists(_musicDir))
        {
            _logger.LogWarning("Scan failed: music directory {MusicDir} does not exist", _musicDir);
            return 0;
        }

        var allowedExtensions = new[] { ".mp3", ".wav", ".flac", ".m4a", ".ogg" };
        var files = Directory.GetFiles(_musicDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => allowedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        _logger.LogInformation("Scanning {Count} audio files in {Directory}", files.Count, _musicDir);

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

                _logger.LogDebug("Indexed: {Artist} - {Title}", track.ArtistName, track.TrackTitle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to index file: {FilePath}", file);
            }
        }

        _logger.LogInformation("Scan completed: indexed {Indexed} new tracks out of {Total} files", indexed, files.Count);
        return indexed;
    }

    public Task IncrementPlayCountAsync(string trackId)
    {
        try
        {
            _db.UpdatePlayCount(trackId);

            if (_cache.TryGetValue(trackId, out var track))
            {
                track.PlayCount++;
            }

            _logger.LogDebug("Incremented play count for track {TrackId}", trackId[..8]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to increment play count for track {TrackId}", trackId[..8]);
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

                _logger.LogDebug("Extracted cover image from {FilePath}", filePath);
            }

            return track;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract metadata from {FilePath}, using fallback", filePath);

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