using DataAccessLayer.Models;
using DataAccessLayer.UnitOfWork;
using WebApi.Errors;
using WebApi.Models;

namespace WebApi.Services;

public class ScheduleService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ScheduleService> _logger;

    public ScheduleService(IUnitOfWork unitOfWork, ILogger<ScheduleService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<ScheduleResponse> GetAsync(
        long userId,
        DateTime from,
        DateTime to,
        long? roomId,
        int limit,
        string? continuationToken,
        CancellationToken ct)
    {
        ScheduleCursor? after = null;

        if (continuationToken != null)
        {
            // решение: токен привязан к периоду и залу; с другими параметрами он не имеет смысла,
            // поэтому отклоняется с 400, а не продолжает чужую выборку.
            if (!ContinuationToken.TryDecode(continuationToken, out var token)
                || !token!.MatchesQuery(from, to, roomId))
            {
                throw ApiException.BadRequest("Continuation token is invalid.");
            }

            after = new ScheduleCursor(token.StartsAt, token.ClassId);
        }

        var rows = await _unitOfWork.Class.GetScheduleAsync(from, to, roomId, after, limit + 1, userId, ct);
        var page = rows.Take(limit).ToList();

        var nextToken = rows.Count > limit
            ? new ContinuationToken(page[^1].StartsAt, page[^1].ClassId, from, to, roomId).Encode()
            : null;

        _logger.LogDebug(
            "Schedule {From}..{To} room {RoomId} limit {Limit} after {Cursor}: {Count} items, more pages: {HasMore}",
            from,
            to,
            roomId,
            limit,
            after,
            page.Count,
            nextToken != null);

        var items = page
            .Select(x => new ScheduleItemResponse(
                x.ClassId,
                x.Title,
                x.StartsAt,
                x.DurationMinutes,
                new RoomResponse(x.RoomId, x.RoomName),
                x.Capacity,
                Math.Max(0, x.Capacity - x.BookedSeats),
                x.MySeats))
            .ToList();

        return new ScheduleResponse(items, nextToken);
    }
}
