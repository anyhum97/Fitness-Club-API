using Serilog.Context;
using WebApi.Infrastructure;

namespace WebApi.Middleware;

public class UserLogContextMiddleware
{
    private readonly RequestDelegate _next;

    public UserLogContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        using (LogContext.PushProperty("UserId", RequestTrace.GetUserId(context) ?? "anonymous"))
        {
            await _next(context);
        }
    }
}
