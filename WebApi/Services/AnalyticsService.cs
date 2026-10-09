using DataAccessLayer.UnitOfWork;
using WebApi.Models;

namespace WebApi.Services;

public class AnalyticsService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<AnalyticsService> _logger;

    public AnalyticsService(IUnitOfWork unitOfWork, ILogger<AnalyticsService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<RoomLoadResponse> GetRoomLoadAsync(
        DateOnly from,
        DateOnly to,
        long? roomId,
        CancellationToken ct)
    {
        var rows = await _unitOfWork.Class.GetRoomLoadAsync(from, to, roomId, ct);

        _logger.LogDebug("Room load {From}..{To} room {RoomId}: {Count} rows", from, to, roomId, rows.Count);

        var items = rows
            .Select(x => new RoomLoadItemResponse(
                x.Date,
                new RoomResponse(x.RoomId, x.RoomName),
                x.ClassesCount,
                x.TotalCapacity,
                x.BookedSeats))
            .ToList();

        return new RoomLoadResponse(items);
    }
}
