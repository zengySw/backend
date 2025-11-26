using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Soundy.Backend.Middleware;

public class RateLimitingMiddleware : IDisposable
{
    private readonly RequestDelegate _next;
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, Counter> _counters = new();
    private readonly Timer _cleanupTimer;
    private readonly ILogger<RateLimitingMiddleware>? _logger;

    private class Counter
    {
        public int Count;
        public DateTime WindowStart;
        public DateTime LastAccess; // Для отслеживания активности
    }

    public RateLimitingMiddleware(
        RequestDelegate next,
        int limit,
        TimeSpan window,
        ILogger<RateLimitingMiddleware>? logger = null)
    {
        _next = next;
        _limit = limit;
        _window = window;
        _logger = logger;

        // Очистка старых записей каждые 5 минут
        _cleanupTimer = new Timer(
            CleanupOldCounters,
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5)
        );

        _logger?.LogInformation("Rate limiting initialized: {Limit} requests per {Window}", limit, window);
    }

    private void CleanupOldCounters(object? state)
    {
        try
        {
            var now = DateTime.UtcNow;
            var expirationTime = TimeSpan.FromHours(1); // IP неактивен больше 1 часа

            var keysToRemove = _counters
                .Where(kvp => now - kvp.Value.LastAccess > expirationTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _counters.TryRemove(key, out _);
            }

            if (keysToRemove.Count > 0)
            {
                _logger?.LogInformation("Cleaned {Count} inactive rate limit entries", keysToRemove.Count);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during rate limit cleanup");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var counter = _counters.GetOrAdd(ip, _ => new Counter
        {
            Count = 0,
            WindowStart = DateTime.UtcNow,
            LastAccess = DateTime.UtcNow
        });

        lock (counter)
        {
            var now = DateTime.UtcNow;
            counter.LastAccess = now; // Обновляем время последнего доступа

            // Сброс счетчика если окно прошло
            if (now - counter.WindowStart > _window)
            {
                counter.WindowStart = now;
                counter.Count = 0;
            }

            counter.Count++;

            if (counter.Count > _limit)
            {
                _logger?.LogWarning("Rate limit exceeded for IP {IP}: {Count} requests", ip, counter.Count);

                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Too Many Requests",
                    message = $"Rate limit exceeded. Maximum {_limit} requests per {_window.TotalSeconds} seconds",
                    retryAfter = (int)(counter.WindowStart.Add(_window) - now).TotalSeconds
                });
                return;
            }
        }

        await _next(context);
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _logger?.LogInformation("Rate limiting middleware disposed");
    }
}