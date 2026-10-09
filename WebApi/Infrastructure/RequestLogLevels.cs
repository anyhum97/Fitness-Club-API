using Serilog.Events;

namespace WebApi.Infrastructure;

public static class RequestLogLevels
{
    public static LogEventLevel Get(HttpContext context, double elapsedMilliseconds, Exception? exception)
    {
        if (exception != null)
        {
            // решение: необработанное исключение уже записано ExceptionHandlingMiddleware уровнем Error
            // со стеком и traceId; итоговую строку запроса оставляем в Debug, чтобы стек не дублировался.
            return LogEventLevel.Debug;
        }

        if (context.Request.Path.StartsWithSegments("/health") || context.Request.Path.StartsWithSegments("/swagger"))
        {
            return LogEventLevel.Debug;
        }

        return context.Response.StatusCode switch
        {
            >= 500 => LogEventLevel.Error,
            StatusCodes.Status429TooManyRequests => LogEventLevel.Warning,
            _ => LogEventLevel.Information
        };
    }
}
