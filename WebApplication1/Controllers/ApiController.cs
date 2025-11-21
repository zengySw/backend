using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Soundy.Backend.Models;
using Soundy.Backend.Services;

namespace Soundy.Backend.Controllers;

[ApiController]
[Route("api/v1")]
public class ApiController : ControllerBase
{
    private readonly IAuthService _authService;

    public ApiController(IAuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        try
        {
            var (token, expiresAt, user) = await _authService.LoginAsync(req.Username, req.Password);

            var resp = new LoginResponse
            {
                Token = token,
                ExpiresAt = expiresAt,
                User = new UserInfo
                {
                    Id = user.Id,
                    Username = user.Username
                }
            };

            return Ok(resp);
        }
        catch
        {
            return Unauthorized(new ErrorResponse
            {
                Error = "Unauthorized",
                Message = "Invalid credentials",
                Code = 401
            });
        }
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "healthy",
            time = DateTime.UtcNow
        });
    }
}
