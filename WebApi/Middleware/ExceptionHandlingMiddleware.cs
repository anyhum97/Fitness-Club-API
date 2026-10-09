using Microsoft.AspNetCore.Mvc;
using Serilog.Context;
using WebApi.Errors;
using WebApi.Infrastructure;

namespace WebApi.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetailsService)
    {
        var traceId = RequestTrace.GetTraceId(context);

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[RequestTrace.HeaderName] = traceId;

            return Task.CompletedTask;
        });

        using var methodScope = LogContext.PushProperty("RequestMethod", context.Request.Method);
        using var pathScope = LogContext.PushProperty("RequestPath", context.Request.Path.Value);

        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Request {Method} {Path} was aborted by the client",
                context.Request.Method,
                context.Request.Path);
        }
        catch (ApiException exception) when (!context.Response.HasStarted)
        {
            LogRejection(context, exception);

            context.Response.Clear();
            context.Response.StatusCode = exception.StatusCode;

            var problemDetails = new ProblemDetails
            {
                Status = exception.StatusCode,
                Title = exception.Title,
                Detail = exception.Detail
            };

            if (exception.Reason != null)
            {
                problemDetails.Extensions["reason"] = exception.Reason;
            }

            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = problemDetails
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Unhandled exception while processing {Method} {Path} for user {UserId}, trace {TraceId}",
                context.Request.Method,
                context.Request.Path,
                RequestTrace.GetUserId(context) ?? "anonymous",
                traceId);

            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Internal server error"
                }
            });
        }
    }

    private void LogRejection(HttpContext context, ApiException exception)
    {
        var level = exception.StatusCode == StatusCodes.Status409Conflict ? LogLevel.Warning : LogLevel.Information;

        _logger.Log(
            level,
            "Request {Method} {Path} rejected with {StatusCode} {Reason}: {Detail}",
            context.Request.Method,
            context.Request.Path,
            exception.StatusCode,
            exception.Reason ?? "-",
            exception.Detail ?? exception.Title);
    }
}
