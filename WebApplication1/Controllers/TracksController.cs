using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Soundy.Backend.Models;
using Soundy.Backend.Services;

namespace Soundy.Backend.Controllers;

[ApiController]
[Route("api/v1")]
public class TracksController : ControllerBase
{
    private readonly ITrackService _trackService;

    public TracksController(ITrackService trackService)
    {
        _trackService = trackService;
    }

    /// <summary>
    /// Получить список треков с пагинацией
    /// </summary>
    /// <param name="limit">Количество треков на странице (по умолчанию 50)</param>
    /// <param name="offset">Смещение (по умолчанию 0)</param>
    [HttpGet("tracks")]
    public async Task<IActionResult> GetTracks([FromQuery] int? limit, [FromQuery] int? offset)
    {
        var l = limit.GetValueOrDefault(50);
        var o = offset.GetValueOrDefault(0);

        var tracks = await _trackService.GetAllTracksAsync(l, o);
        var total = await _trackService.GetTotalTracksCountAsync(); // ✅ ПРАВИЛЬНЫЙ total

        return Ok(new TrackResponse
        {
            Tracks = tracks,
            Total = total
        });
    }

    /// <summary>
    /// Получить трек по ID
    /// </summary>
    [HttpGet("tracks/{id}")]
    public async Task<IActionResult> GetTrack(string id)
    {
        var track = await _trackService.GetTrackAsync(id);
        if (track == null)
        {
            return NotFound(new ErrorResponse
            {
                Error = "Not Found",
                Message = "Track not found",
                Code = 404
            });
        }

        return Ok(track);
    }

    /// <summary>
    /// Поиск треков по названию, исполнителю или альбому
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int? limit, [FromQuery] int? offset)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest(new ErrorResponse
            {
                Error = "Bad Request",
                Message = "Query parameter 'q' is required",
                Code = 400
            });
        }

        var l = limit.GetValueOrDefault(50);
        var o = offset.GetValueOrDefault(0);

        var tracks = await _trackService.SearchTracksAsync(q, l, o);
        return Ok(new TrackResponse
        {
            Tracks = tracks,
            Total = tracks.Count
        });
    }

    /// <summary>
    /// Стриминг аудио файла
    /// </summary>
    [HttpGet("stream")]
    public async Task<IActionResult> Stream([FromQuery] string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new ErrorResponse
            {
                Error = "Bad Request",
                Message = "Track ID is required",
                Code = 400
            });
        }

        var track = await _trackService.GetTrackAsync(id);
        if (track == null)
        {
            return NotFound(new ErrorResponse
            {
                Error = "Not Found",
                Message = "Track not found",
                Code = 404
            });
        }

        _ = _trackService.IncrementPlayCountAsync(id); // fire-and-forget

        var filePath = track.FilePath;

        // Определяем Content-Type по расширению
        var contentType = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".m4a" => "audio/mp4",
            ".ogg" => "audio/ogg",
            _ => "application/octet-stream"
        };

        // FileStream + range support
        var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(fs, contentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// Загрузить новый трек (требуется авторизация)
    /// </summary>
    [HttpPost("upload")]
    [Authorize]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> Upload(
        [FromForm(Name = "audio")] IFormFile audio,
        [FromForm(Name = "track_artist")] string? trackArtist,
        [FromForm(Name = "track_title")] string? trackTitle,
        [FromForm(Name = "track_album")] string? trackAlbum,
        [FromForm(Name = "cover")] IFormFile? cover)
    {
        if (audio == null)
        {
            return BadRequest(new ErrorResponse
            {
                Error = "Bad Request",
                Message = "Audio file is required",
                Code = 400
            });
        }

        var meta = new CreateTrackRequest
        {
            ArtistName = trackArtist,
            TrackTitle = trackTitle,
            Album = trackAlbum
        };

        try
        {
            var track = await _trackService.UploadTrackAsync(audio, meta, cover);
            return StatusCode(201, new SuccessResponse
            {
                Success = true,
                Message = "Track uploaded successfully",
                Data = track
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new ErrorResponse
            {
                Error = "Internal Server Error",
                Message = $"Upload failed: {ex.Message}",
                Code = 500
            });
        }
    }

    /// <summary>
    /// Удалить трек (требуется авторизация)
    /// </summary>
    [HttpDelete("tracks/{id}")]
    [Authorize]
    public async Task<IActionResult> Delete(string id)
    {
        try
        {
            await _trackService.DeleteTrackAsync(id);
            return Ok(new SuccessResponse
            {
                Success = true,
                Message = "Track deleted successfully"
            });
        }
        catch (FileNotFoundException)
        {
            return NotFound(new ErrorResponse
            {
                Error = "Not Found",
                Message = "Track not found",
                Code = 404
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new ErrorResponse
            {
                Error = "Internal Server Error",
                Message = $"Delete failed: {ex.Message}",
                Code = 500
            });
        }
    }

    /// <summary>
    /// Сканировать директорию с музыкой и добавить новые треки (требуется авторизация)
    /// </summary>
    [HttpPost("scan")]
    [Authorize]
    public async Task<IActionResult> Scan()
    {
        try
        {
            var indexed = await _trackService.ScanDirectoryAsync();
            return Ok(new SuccessResponse
            {
                Success = true,
                Message = $"Indexed {indexed} new tracks",
                Data = new { indexed }
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new ErrorResponse
            {
                Error = "Internal Server Error",
                Message = $"Scan failed: {ex.Message}",
                Code = 500
            });
        }
    }

    /// <summary>
    /// Экспортировать список треков в CSV (требуется авторизация)
    /// </summary>
    [HttpGet("export/csv")]
    [Authorize]
    public async Task<IActionResult> ExportCsv()
    {
        var tracks = await _trackService.GetAllTracksAsync(10000, 0);

        var sb = new StringBuilder();
        sb.AppendLine("Track ID,Artist,Title,Album,Duration,Play Count");

        foreach (var t in tracks)
        {
            sb.AppendLine($"{EscapeCsv(t.TrackId)},{EscapeCsv(t.ArtistName)},{EscapeCsv(t.TrackTitle)},{EscapeCsv(t.Album)},{t.DurationSeconds},{t.PlayCount}");
        }

        var bytes = Encoding.UTF8.GetBytes("\uFEFF" + sb.ToString());
        return File(bytes, "text/csv; charset=utf-8", "soundy_export.csv");
    }

    /// <summary>
    /// Экспортировать список треков в JSON (требуется авторизация)
    /// </summary>
    [HttpGet("export/json")]
    [Authorize]
    public async Task<IActionResult> ExportJson()
    {
        var tracks = await _trackService.GetAllTracksAsync(10000, 0);
        return File(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { tracks, total = tracks.Count }),
            "application/json",
            "soundy_export.json"
        );
    }

    private static string EscapeCsv(string? value)
    {
        value ??= "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            value = value.Replace("\"", "\"\"");
            return $"\"{value}\"";
        }
        return value;
    }
}