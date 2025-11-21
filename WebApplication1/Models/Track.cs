using System;

namespace Soundy.Backend.Models;

public class Track
{
    public string TrackId { get; set; } = default!;
    public string ArtistName { get; set; } = default!;
    public string TrackTitle { get; set; } = default!;
    public string Album { get; set; } = "";
    public string Genre { get; set; } = "";
    public int Year { get; set; }
    public int DurationSeconds { get; set; }
    public string FilePath { get; set; } = default!;
    public string? CoverUrl { get; set; }
    public long FileSize { get; set; }
    public string Format { get; set; } = "";
    public int Bitrate { get; set; }
    public int PlayCount { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
