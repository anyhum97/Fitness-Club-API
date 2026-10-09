using System.Diagnostics;
using System.Security.Claims;

namespace WebApi.Infrastructure;

public static class RequestTrace
{
    public const string HeaderName = "X-Trace-Id";

    public static string GetTraceId(HttpContext context)
    {
        return Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
    }

    public static string? GetUserId(HttpContext context)
    {
        return context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
