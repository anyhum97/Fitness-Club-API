using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Errors;
using WebApi.Infrastructure;
using WebApi.Models;
using WebApi.RateLimiting;
using WebApi.Services;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[RateLimited]
[Route("schedule")]
public class ScheduleController : BaseApiController
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    private readonly ScheduleService _schedule;

    public ScheduleController(ScheduleService schedule)
    {
        _schedule = schedule;
    }

    [HttpGet]
    public async Task<ActionResult<ScheduleResponse>> Get([FromQuery] ScheduleQuery query, CancellationToken ct)
    {
        if (!RequestParsing.TryParseMoment(query.From, out var from))
        {
            throw ApiException.BadRequest("Parameter 'from' is required and must be an ISO 8601 date and time.");
        }

        if (!RequestParsing.TryParseMoment(query.To, out var to))
        {
            throw ApiException.BadRequest("Parameter 'to' is required and must be an ISO 8601 date and time.");
        }

        if (to <= from)
        {
            throw ApiException.BadRequest("Parameter 'to' must be later than 'from'.");
        }

        var roomId = ParseOptionalRoomId(query.RoomId);
        var limit = DefaultLimit;

        if (query.Limit != null
            && (!RequestParsing.TryParseInt(query.Limit, out limit) || limit < 1 || limit > MaxLimit))
        {
            throw ApiException.BadRequest($"Parameter 'limit' must be a number from 1 to {MaxLimit}.");
        }

        return await _schedule.GetAsync(CurrentUserId, from, to, roomId, limit, query.ContinuationToken, ct);
    }
}
