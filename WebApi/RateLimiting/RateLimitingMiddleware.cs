using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace WebApi.RateLimiting;

public class RateLimitingMiddleware
{
    private const string IncrementScript = """
        local count = redis.call('INCR', KEYS[1])
        if count == 1 then
            redis.call('EXPIRE', KEYS[1], ARGV[1])
        end
        return count
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

        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var window = now / _options.WindowSeconds;
        var key = $"rate-limit:{userId}:{window}";

        var count = (long)await _redis.GetDatabase().ScriptEvaluateAsync(
            IncrementScript,
            [key],
            [_options.WindowSeconds + 1]);

        if (count <= _options.PermitLimit)
        {
            await _next(context);

            return;
        }

        var retryAfter = (window + 1) * _options.WindowSeconds - now;

        logger.LogWarning(
            "Rate limit exceeded by user {UserId}: request {Count} of {PermitLimit} in the current window, "
            + "retry after {RetryAfter} s",
            userId,
            count,
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
