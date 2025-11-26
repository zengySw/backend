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

public class CreateTrackRequest
{
    public string? ArtistName { get; set; }
    public string? TrackTitle { get; set; }
    public string? Album { get; set; }
}

public class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class LoginResponse
{
    public string Token { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public UserInfo User { get; set; } = new();
}

public class UserInfo
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
}

public class TrackResponse
{
    public List<Track> Tracks { get; set; } = new();
    public int Total { get; set; }
}

public class ErrorResponse
{
    public string Error { get; set; } = "";
    public string Message { get; set; } = "";
    public int Code { get; set; }
}

public class SuccessResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public object? Data { get; set; }
}