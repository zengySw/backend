using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Soundy.Backend.Models;
using Soundy.Backend.Services;

namespace soundy.Controllers;

[ApiController]
[Route("api/v1")]
public class ApiController(IAuthService authService) : ControllerBase
{
    private readonly IAuthService _authService = authService;

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        try
        {
            // Fully qualify the User type to avoid ambiguity
            (string token, DateTime expiresAt, Soundy.Backend.Models.User user) = await _authService.LoginAsync(req.Username, req.Password);

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