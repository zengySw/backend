using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Soundy.Backend.Middleware;

public class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly int _limit;
    private readonly TimeSpan _window;

    private class Counter
    {
        public int Count;
        public DateTime WindowStart;
    }

    private readonly ConcurrentDictionary<string, Counter> _counters = new();

    public RateLimitingMiddleware(RequestDelegate next, int limit, TimeSpan window)
    {
        _next = next;
        _limit = limit;
        _window = window;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var counter = _counters.GetOrAdd(ip, _ => new Counter { Count = 0, WindowStart = DateTime.UtcNow });
        lock (counter)
        {
            var now = DateTime.UtcNow;
            if (now - counter.WindowStart > _window)
            {
                counter.WindowStart = now;
                counter.Count = 0;
            }

            counter.Count++;
            if (counter.Count > _limit)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsync("Too many requests");
                return;
            }
        }

        await _next(context);
    }
}