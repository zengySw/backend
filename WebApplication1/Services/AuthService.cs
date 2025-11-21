// IAuthService.cs
using System;
using System.Threading.Tasks;
using Soundy.Backend.Models;

namespace Soundy.Backend.Services;

public interface IAuthService
{
    Task<(string Token, DateTime ExpiresAt, User User)> LoginAsync(string username, string password);
    Task CreateUserAsync(string username, string password);
}
