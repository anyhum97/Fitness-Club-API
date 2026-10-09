namespace WebApi.Errors;

public class ApiException : Exception
{
    public ApiException(int statusCode, string title, string? reason = null, string? detail = null)
        : base(detail ?? title)
    {
        StatusCode = statusCode;
        Title = title;
        Reason = reason;
        Detail = detail;
    }

    public int StatusCode { get; }

    public string Title { get; }

    public string? Reason { get; }

    public string? Detail { get; }

    public static ApiException BadRequest(string detail)
    {
        return new ApiException(StatusCodes.Status400BadRequest, "Bad Request", detail: detail);
    }

    public static ApiException NotFound(string detail)
    {
        return new ApiException(StatusCodes.Status404NotFound, "Not Found", detail: detail);
    }

    public static ApiException Conflict(string reason)
    {
        return new ApiException(StatusCodes.Status409Conflict, "Conflict", reason);
    }
}
