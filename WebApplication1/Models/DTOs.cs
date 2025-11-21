using System;
using System.Collections.Generic;

namespace Soundy.Backend.Models;

public class CreateTrackRequest
{
    public string? ArtistName { get; set; }
    public string? TrackTitle { get; set; }
    public string? Album { get; set; }
}

public class TrackResponse
{
    public List<Track> Tracks { get; set; } = new();
    public int Total { get; set; }
}

public class LoginRequest
{
    public string Username { get; set; } = default!;
    public string Password { get; set; } = default!;
}

public class LoginResponse
{
    public string Token { get; set; } = default!;
    public DateTime ExpiresAt { get; set; }
    public UserInfo User { get; set; } = default!;
}

public class UserInfo
{
    public int Id { get; set; }
    public string Username { get; set; } = default!;
}

public class ErrorResponse
{
    public string Error { get; set; } = default!;
    public string Message { get; set; } = default!;
    public int Code { get; set; }
}

public class SuccessResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = default!;
    public object? Data { get; set; }
}
