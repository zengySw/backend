using System;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Soundy.Backend.Data;
using Soundy.Backend.Middleware;
using Soundy.Backend.Services;

var builder = WebApplication.CreateBuilder(args);

var cfg = builder.Configuration;

// --- Config values ---
var dbPath = cfg["Database:Path"] ?? "./data/soundy.db";
var musicDir = cfg["Storage:MusicDir"] ?? "./data/music";
var coversDir = cfg["Storage:CoversDir"] ?? "./data/covers";
var uploadMaxSize = long.Parse(cfg["Storage:UploadMaxSize"] ?? "104857600");

var jwtSecret = cfg["Security:JWTSecret"] ?? "your-secret-key-change-in-production";
var adminUsername = cfg["Security:AdminUsername"] ?? "admin";
var adminPassword = cfg["Security:AdminPassword"] ?? "admin123";
var sessionDurationHours = int.Parse(cfg["Security:SessionDurationHours"] ?? "24");

var rateRequests = int.Parse(cfg["RateLimit:Requests"] ?? "100");
var rateWindowSeconds = int.Parse(cfg["RateLimit:WindowSeconds"] ?? "60");

var corsOrigins = cfg.GetSection("CORS:Origins").Get<string[]>() ?? Array.Empty<string>();
var allowCredentials = bool.Parse(cfg["CORS:AllowCredentials"] ?? "true");

// --- Services ---
builder.Services.AddSingleton(_ => new SoundyDb(dbPath));
builder.Services.AddSingleton<ITrackService>(sp =>
{
    var db = sp.GetRequiredService<SoundyDb>();
    return new TrackService(db, musicDir, coversDir, uploadMaxSize);
});
builder.Services.AddSingleton<IAuthService>(sp =>
{
    var db = sp.GetRequiredService<SoundyDb>();
    return new AuthService(db, jwtSecret, TimeSpan.FromHours(sessionDurationHours));
});

builder.Services.AddControllers();

// JWT auth
var key = Encoding.UTF8.GetBytes(jwtSecret);

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ClockSkew = TimeSpan.Zero
        };
    });

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("SoundyCors", policy =>
    {
        policy.WithOrigins(corsOrigins);
        policy.WithHeaders("Content-Type", "Authorization");
        policy.WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS");

        if (allowCredentials)
            policy.AllowCredentials();
        else
            policy.DisallowCredentials();
    });
});

var app = builder.Build();

// Logging, recovery — у ASP.NET свое, плюс можно добавить
app.UseRouting();
app.UseCors("SoundyCors");

app.UseAuthentication();
app.UseMiddleware<RateLimitingMiddleware>(rateRequests, TimeSpan.FromSeconds(rateWindowSeconds));
app.UseAuthorization();

// static covers (/covers/...)
Directory.CreateDirectory(coversDir);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(coversDir)),
    RequestPath = "/covers"
});

// Map controllers
app.MapControllers();

// Init services: track cache + admin user
using (var scope = app.Services.CreateScope())
{
    var trackService = scope.ServiceProvider.GetRequiredService<ITrackService>();
    await trackService.InitializeAsync();

    var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
    await auth.CreateUserAsync(adminUsername, adminPassword);

    Console.WriteLine($"Admin credentials: {adminUsername} / {adminPassword}");
}

var host = cfg["Server:Host"] ?? "0.0.0.0";
var port = cfg["Server:Port"] ?? "8000";

app.Urls.Add($"http://{host}:{port}");

app.Run();
