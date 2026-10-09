using DataAccessLayer.Configurations;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using WebApi.Errors;
using WebApi.Infrastructure;
using WebApi.Models;

namespace WebApi.Services;

public class WaitlistService
{
    private const string WaitingUserClassIndexName = "ux_waitlist_entries_waiting_user_class";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IdempotencyService _idempotency;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WaitlistService> _logger;

    public WaitlistService(
        IUnitOfWork unitOfWork,
        IdempotencyService idempotency,
        TimeProvider timeProvider,
        ILogger<WaitlistService> logger)
    {
        _unitOfWork = unitOfWork;
        _idempotency = idempotency;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<WaitlistEntryResponse> JoinAsync(
        long userId,
        long classId,
        int seats,
        string idempotencyKey,
        CancellationToken ct)
    {
        var idempotencyRequest = new IdempotencyRequest(
            userId,
            IdempotencyService.WaitlistScope,
            idempotencyKey,
            $"{classId}:{seats}");

        try
        {
            var response = await _unitOfWork.ExecuteInTransactionAsync(
                token => JoinInTransactionAsync(idempotencyRequest, classId, seats, token),
                ct);

            _logger.LogInformation(
                "User {UserId} waits for {Seats} seats on class {ClassId} as entry {EntryId} at position {Position}",
                userId,
                response.Seats,
                response.ClassId,
                response.Id,
                response.Position);

            return response;
        }
        catch (Exception exception) when (UnitOfWork.IsUniqueViolation(
            exception,
            IdempotencyRecordConfiguration.UniqueKeyIndexName))
        {
            return await _idempotency.FindReplayAsync<WaitlistEntryResponse>(idempotencyRequest, ct)
                ?? throw new InvalidOperationException("Idempotency record disappeared after a unique violation.");
        }
        catch (Exception exception) when (UnitOfWork.IsUniqueViolation(exception, WaitingUserClassIndexName))
        {
            throw ApiException.Conflict(ConflictReason.AlreadyWaiting);
        }
    }

    public async Task<CancelWaitlistEntryResponse> LeaveAsync(long userId, long entryId, CancellationToken ct)
    {
        var response = await _unitOfWork.ExecuteInTransactionAsync(
            token => LeaveInTransactionAsync(userId, entryId, token),
            ct);

        _logger.LogInformation("User {UserId} left the waitlist, entry {EntryId}", userId, entryId);

        return response;
    }

    private async Task<WaitlistEntryResponse> JoinInTransactionAsync(
        IdempotencyRequest idempotencyRequest,
        long classId,
        int seats,
        CancellationToken ct)
    {
        var lockedClass = await _unitOfWork.Class.GetForUpdateAsync(classId, ct)
            ?? throw ApiException.NotFound("Class not found.");

        var replay = await _idempotency.FindReplayAsync<WaitlistEntryResponse>(idempotencyRequest, ct);

        if (replay != null)
        {
            return replay;
        }

        var now = _timeProvider.GetUtcNowTruncated();
        var userId = idempotencyRequest.UserId;

        if (lockedClass.IsCancelled)
        {
            throw ApiException.Conflict(ConflictReason.ClassCancelled);
        }

        if (now >= lockedClass.StartsAt)
        {
            throw ApiException.Conflict(ConflictReason.ClassStarted);
        }

        if (await _unitOfWork.Waitlist.IsWaitingAsync(userId, classId, ct))
        {
            throw ApiException.Conflict(ConflictReason.AlreadyWaiting);
        }

        var freeSeats = lockedClass.Capacity - await _unitOfWork.Enrollment.SumActiveSeatsAsync(classId, ct);

        if (freeSeats >= seats)
        {
            throw ApiException.Conflict(ConflictReason.SeatsAvailable);
        }

        // решение: абонемент при постановке в очередь не проверяется и не списывается — это делается
        // в момент продвижения, когда ожидание превращается в бронь.
        var entry = new WaitlistEntry
        {
            UserId = userId,
            ClassId = classId,
            Seats = seats,
            Status = WaitlistStatus.Waiting,
            CreatedAt = now
        };

        await _unitOfWork.Waitlist.CreateAsync(entry, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        var position = await _unitOfWork.Waitlist.GetPositionAsync(entry, ct);
        var response = new WaitlistEntryResponse(entry.Id, entry.ClassId, entry.Seats, position, entry.CreatedAt);

        await _idempotency.SaveAsync(idempotencyRequest, response, ct);

        return response;
    }

    private async Task<CancelWaitlistEntryResponse> LeaveInTransactionAsync(
        long userId,
        long entryId,
        CancellationToken ct)
    {
        var entry = await _unitOfWork.Waitlist.GetOwnedAsync(entryId, userId, ct)
            ?? throw ApiException.NotFound("Waitlist entry not found.");

        await _unitOfWork.Class.GetForUpdateAsync(entry.ClassId, ct);
        await _unitOfWork.DbContext.Entry(entry).ReloadAsync(ct);

        // решение: ожидание, которое уже превратилось в бронь, из очереди не убирается — бронь
        // отменяется своим эндпоинтом; отвечаем 409 waitlist-fulfilled.
        if (entry.Status == WaitlistStatus.Promoted)
        {
            throw ApiException.Conflict(ConflictReason.WaitlistFulfilled);
        }

        if (entry.Status == WaitlistStatus.Waiting)
        {
            entry.Status = WaitlistStatus.Cancelled;
            entry.CancelledAt = _timeProvider.GetUtcNowTruncated();
        }

        return new CancelWaitlistEntryResponse(entry.Id, WaitlistStatus.Cancelled);
    }
}
