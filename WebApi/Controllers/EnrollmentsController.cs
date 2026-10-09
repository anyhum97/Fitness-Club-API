using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Models;
using WebApi.RateLimiting;
using WebApi.Services;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[RateLimited]
[Route("enrollments")]
public class EnrollmentsController : BaseApiController
{
    private readonly EnrollmentService _enrollments;
    private readonly EnrollmentCardService _cards;

    public EnrollmentsController(EnrollmentService enrollments, EnrollmentCardService cards)
    {
        _enrollments = enrollments;
        _cards = cards;
    }

    [HttpPost]
    public async Task<ActionResult<EnrollmentResponse>> Create(
        [FromBody] CreateEnrollmentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var key = RequireIdempotencyKey(idempotencyKey);

        var response = await _enrollments.CreateAsync(
            CurrentUserId,
            request.ClassId!.Value,
            request.Seats!.Value,
            key,
            ct);

        return CreatedAtAction(nameof(Get), new { id = response.Id }, response);
    }

    [HttpPost("{id}/seats")]
    public async Task<ActionResult<EnrollmentCardResponse>> ChangeSeats(
        long id,
        [FromBody] ChangeSeatsRequest request,
        CancellationToken ct)
    {
        return await _enrollments.ChangeSeatsAsync(CurrentUserId, id, request.Seats!.Value, ct);
    }

    [HttpPost("{id}/cancel")]
    public async Task<ActionResult<CancelEnrollmentResponse>> Cancel(long id, CancellationToken ct)
    {
        return await _enrollments.CancelAsync(CurrentUserId, id, ct);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<EnrollmentCardResponse>> Get(long id, CancellationToken ct)
    {
        return await _cards.GetAsync(id, CurrentUserId, ct);
    }
}
