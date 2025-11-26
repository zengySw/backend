using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.OpenApi.Models;
using Soundy.Backend.Controllers;
using Soundy.Backend.Data;
using Soundy.Backend.Models;
using Soundy.Backend.Services;
using Soundy.Backend.Middleware;
using System.Text;
using System.IO.Compression;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// Configuration
var dbPath = cfg["Database:Path"] ?? "./Data/soundy.db";
var musicDir = cfg["Storage:MusicDir"] ?? "./Data/music";
var coversDir = cfg["Storage:CoversDir"] ?? "./Data/covers";
var uploadMaxSize = long.Parse(cfg["Storage:UploadMaxSize"] ?? "104857600");

var jwtSecret = cfg["Security:JwtSecret"] ?? "your-secret-key-change-in-production";
var adminUsername = cfg["Security:AdminUsername"] ?? "admin";
var adminPassword = cfg["Security:AdminPassword"] ?? "admin123";
var sessionDurationHours = int.Parse(cfg["Security:SessionDurationHours"] ?? "24");

var rateRequests = int.Parse(cfg["RateLimit:Requests"] ?? "100");
var rateWindowSeconds = int.Parse(cfg["RateLimit:WindowSeconds"] ?? "60");

var corsOrigins = cfg.GetSection("CORS:Origins").Get<string[]>() ?? new[] { "http://localhost:3000", "http://localhost:8080" };
var allowCredentials = bool.Parse(cfg["CORS:AllowCredentials"] ?? "true");

// --- Services ---
builder.Services.AddSingleton(_ => new SoundyDb(dbPath));
builder.Services.AddSingleton<ITrackService>(sp =>
{
    var db = sp.GetRequiredService<SoundyDb>();
    var logger = sp.GetRequiredService<ILogger<TrackService>>();
    return new TrackService(db, musicDir, coversDir, uploadMaxSize, logger);
});
builder.Services.AddSingleton<IAuthService>(sp =>
{
    var db = sp.GetRequiredService<SoundyDb>();
    return new AuthService(db, jwtSecret, TimeSpan.FromHours(sessionDurationHours));
});

builder.Services.AddControllers();

// ✅ Response Compression (GZIP)
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

// ✅ Swagger Documentation
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Soundy Music API",
        Version = "v1",
        Description = "Music streaming platform REST API"
    });

    // JWT авторизация в Swagger UI
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token."
    });

    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// ✅ Health Checks
builder.Services.AddHealthChecks();

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

// ✅ Swagger (только в Development)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Soundy API V1");
        options.RoutePrefix = "swagger";
    });
}

// ✅ Global Exception Handler
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        var exceptionFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();

        if (exceptionFeature != null)
        {
            logger.LogError(exceptionFeature.Error, "Unhandled exception occurred");

            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";

            // Use anonymous object to avoid ambiguous ErrorResponse type when duplicate definitions exist in the project
            await context.Response.WriteAsJsonAsync(new
            {
                Error = "Internal Server Error",
                Message = app.Environment.IsDevelopment()
                    ? exceptionFeature.Error.Message
                    : "An error occurred processing your request",
                Code = 500
            });
        }
    });
});

// Middleware pipeline
app.UseResponseCompression(); // ✅ GZIP компрессия
app.UseRouting();
app.UseCors("SoundyCors");

app.UseAuthentication();
app.UseMiddleware<RateLimitingMiddleware>(rateRequests, TimeSpan.FromSeconds(rateWindowSeconds));
app.UseAuthorization();

// Static covers (/covers/...)
Directory.CreateDirectory(coversDir);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.GetFullPath(coversDir)),
    RequestPath = "/covers"
});

// Map controllers
app.MapControllers();

// ✅ Health Check endpoint
app.MapHealthChecks("/health");

// Init services: track cache + admin user
using (var scope = app.Services.CreateScope())
{
    var trackService = scope.ServiceProvider.GetRequiredService<ITrackService>();
    var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    await trackService.InitializeAsync();

    var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
    await auth.CreateUserAsync(adminUsername, adminPassword);

    scopedLogger.LogInformation("🎵 Admin credentials: {Username} / {Password}", adminUsername, adminPassword);
}

var host = cfg["Server:Host"] ?? "0.0.0.0";
var port = cfg["Server:Port"] ?? "8080";

app.Urls.Add($"http://{host}:{port}");

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("🚀 Server starting on http://{Host}:{Port}", host, port);
logger.LogInformation("📚 Swagger UI available at http://{Host}:{Port}/swagger", host, port);
logger.LogInformation("💚 Health check available at http://{Host}:{Port}/health", host, port);

app.Run();

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