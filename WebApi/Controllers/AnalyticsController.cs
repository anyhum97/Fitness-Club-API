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
[Route("analytics")]
public class AnalyticsController : BaseApiController
{
    private readonly AnalyticsService _analytics;

    public AnalyticsController(AnalyticsService analytics)
    {
        _analytics = analytics;
    }

    [HttpGet("load")]
    [Authorize(Roles = AdminRole)]
    public async Task<ActionResult<RoomLoadResponse>> GetLoad([FromQuery] RoomLoadQuery query, CancellationToken ct)
    {
        if (!RequestParsing.TryParseDate(query.From, out var from))
        {
            throw ApiException.BadRequest("Parameter 'from' is required and must be a date in the form yyyy-MM-dd.");
        }

        if (!RequestParsing.TryParseDate(query.To, out var to))
        {
            throw ApiException.BadRequest("Parameter 'to' is required and must be a date in the form yyyy-MM-dd.");
        }

        if (to < from)
        {
            throw ApiException.BadRequest("Parameter 'to' must not be earlier than 'from'.");
        }

        return await _analytics.GetRoomLoadAsync(from, to, ParseOptionalRoomId(query.RoomId), ct);
    }
}
