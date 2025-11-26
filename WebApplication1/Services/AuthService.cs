using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;
using Soundy.Backend.Data;
using Soundy.Backend.Models;

namespace Soundy.Backend.Services;

public class AuthService : IAuthService
{
    private readonly SoundyDb _db;
    private readonly string _jwtSecret;
    private readonly TimeSpan _sessionDuration;

    public AuthService(SoundyDb db, string jwtSecret, TimeSpan sessionDuration)
    {
        _db = db;
        _jwtSecret = jwtSecret;
        _sessionDuration = sessionDuration;
    }

    public Task<(string Token, DateTime ExpiresAt, User User)> LoginAsync(string username, string password)
    {
        var user = _db.GetUserByUsername(username);
        if (user == null)
            throw new UnauthorizedAccessException("Invalid credentials");

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            throw new UnauthorizedAccessException("Invalid credentials");

        var expiresAt = DateTime.UtcNow.Add(_sessionDuration);
        var token = GenerateToken(user.Id, expiresAt);

        return Task.FromResult((token, expiresAt, user));
    }

    public Task CreateUserAsync(string username, string password)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(password);
        _db.CreateUser(username, hash);
        return Task.CompletedTask;
    }

    private string GenerateToken(int userId, DateTime expiresAt)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim("user_id", userId.ToString())
        };

        var token = new JwtSecurityToken(
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}