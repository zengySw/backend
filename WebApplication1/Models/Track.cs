namespace Soundy.Backend.Models;

public class Track
{
    public string TrackId { get; set; } = "";
    public string ArtistName { get; set; } = "";
    public string TrackTitle { get; set; } = "";
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public int Year { get; set; }
    public int DurationSeconds { get; set; }
    public string FilePath { get; set; } = "";
    public string? CoverUrl { get; set; }
    public long FileSize { get; set; }
    public string Format { get; set; } = "";
    public int Bitrate { get; set; }
    public int PlayCount { get; set; }
}

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}