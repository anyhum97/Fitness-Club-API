using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Models;
using WebApi.RateLimiting;
using WebApi.Services;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[RateLimited]
[Route("enrollments/waitlist")]
public class WaitlistController : BaseApiController
{
    private readonly WaitlistService _waitlist;

    public WaitlistController(WaitlistService waitlist)
    {
        _waitlist = waitlist;
    }

    [HttpPost]
    public async Task<ActionResult<WaitlistEntryResponse>> Join(
        [FromBody] JoinWaitlistRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var key = RequireIdempotencyKey(idempotencyKey);

        var response = await _waitlist.JoinAsync(
            CurrentUserId,
            request.ClassId!.Value,
            request.Seats!.Value,
            key,
            ct);

        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("{id}/cancel")]
    public async Task<ActionResult<CancelWaitlistEntryResponse>> Leave(long id, CancellationToken ct)
    {
        return await _waitlist.LeaveAsync(CurrentUserId, id, ct);
    }
}
