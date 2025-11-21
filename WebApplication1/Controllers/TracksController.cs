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

    // GET /api/v1/tracks?limit=&offset=
    [HttpGet("tracks")]
    public async Task<IActionResult> GetTracks([FromQuery] int? limit, [FromQuery] int? offset)
    {
        var l = limit.GetValueOrDefault(50);
        var o = offset.GetValueOrDefault(0);

        var tracks = await _trackService.GetAllTracksAsync(l, o);
        return Ok(new TrackResponse
        {
            Tracks = tracks,
            Total = tracks.Count
        });
    }

    // GET /api/v1/tracks/{id}
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

    // GET /api/v1/search?q=&limit=&offset=
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

    // GET /api/v1/stream?id=...
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

        _ = _trackService.IncrementPlayCountAsync(id); // fire-and-forget как goroutine

        var filePath = track.FilePath;
        var contentType = "audio/mpeg"; // можно улучшить по расширению

        // FileStream + range support
        var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(fs, contentType, enableRangeProcessing: true);
    }

    // POST /api/v1/upload (protected)
    [HttpPost("upload")]
    [Authorize]
    [RequestSizeLimit(200_000_000)] // на всякий случай
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

    // DELETE /api/v1/tracks/{id} (protected)
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

    // POST /api/v1/scan (protected)
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

    // GET /api/v1/export/csv (protected)
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

    // GET /api/v1/export/json (protected)
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
