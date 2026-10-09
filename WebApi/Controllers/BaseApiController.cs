using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using WebApi.Errors;
using WebApi.Infrastructure;

namespace WebApi.Controllers;

public abstract class BaseApiController : ControllerBase
{
    protected const string AdminRole = "admin";

    protected long CurrentUserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    protected bool IsAdmin => User.IsInRole(AdminRole);

    protected static string RequireIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw ApiException.BadRequest("Idempotency-Key header is required.");
        }

        return idempotencyKey;
    }

    protected static long? ParseOptionalRoomId(string? roomId)
    {
        if (roomId == null)
        {
            return null;
        }

        if (!RequestParsing.TryParseLong(roomId, out var parsed))
        {
            throw ApiException.BadRequest("Parameter 'roomId' must be a number.");
        }

        return parsed;
    }
}
