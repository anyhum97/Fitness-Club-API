using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace WebApi.RateLimiting;

public class RateLimitingMiddleware
{
    private const string SlidingWindowScript = """
        local key = KEYS[1]
        local now = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        local limit = tonumber(ARGV[3])
        redis.call('ZREMRANGEBYSCORE', key, '-inf', now - window)
        local count = redis.call('ZCARD', key)
        if count < limit then
            redis.call('ZADD', key, now, ARGV[4])
            redis.call('PEXPIRE', key, window)
            return { 1, count + 1, 0 }
        end
        local oldest = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
        return { 0, count, tonumber(oldest[2]) + window - now }
        """;

    private readonly RequestDelegate _next;
    private readonly IConnectionMultiplexer _redis;
    private readonly RateLimitOptions _options;
    private readonly TimeProvider _timeProvider;

    public RateLimitingMiddleware(
        RequestDelegate next,
        IConnectionMultiplexer redis,
        RateLimitOptions options,
        TimeProvider timeProvider)
    {
        _next = next;
        _redis = redis;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IProblemDetailsService problemDetailsService,
        ILogger<RateLimitingMiddleware> logger)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var isLimited = context.GetEndpoint()?.Metadata.GetMetadata<RateLimitedAttribute>() != null;

        if (!isLimited || userId == null)
        {
            await _next(context);

            return;
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var windowMilliseconds = _options.WindowSeconds * 1000L;

        // решение: окно скользящее — считаются запросы за последние 60 секунд от текущего момента,
        // поэтому на стыке календарных минут лимит не удваивается; отклонённые запросы окно не продлевают.
        var result = (RedisResult[])(await _redis.GetDatabase().ScriptEvaluateAsync(
            SlidingWindowScript,
            [$"rate-limit:{userId}"],
            [now, windowMilliseconds, _options.PermitLimit, Guid.NewGuid().ToString("N")]))!;

        if ((long)result[0] == 1)
        {
            await _next(context);

            return;
        }

        var retryAfter = Math.Max(1, (long)Math.Ceiling((long)result[2] / 1000.0));

        logger.LogWarning(
            "Rate limit exceeded by user {UserId}: {Count} requests in the last {WindowSeconds} s, "
            + "limit {PermitLimit}, retry after {RetryAfter} s",
            userId,
            (long)result[1],
            _options.WindowSeconds,
            _options.PermitLimit,
            retryAfter);

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too Many Requests"
            }
        });
    }
}
