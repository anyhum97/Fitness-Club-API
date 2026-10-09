using DataAccessLayer.Configurations;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using WebApi.Errors;
using WebApi.Infrastructure;
using WebApi.Models;

namespace WebApi.Services;

public class EnrollmentService
{
    public static readonly TimeSpan FullRefundNotice = TimeSpan.FromHours(2);

    private readonly IUnitOfWork _unitOfWork;
    private readonly IdempotencyService _idempotency;
    private readonly WaitlistPromoter _promoter;
    private readonly EnrollmentCardService _cards;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EnrollmentService> _logger;

    public EnrollmentService(
        IUnitOfWork unitOfWork,
        IdempotencyService idempotency,
        WaitlistPromoter promoter,
        EnrollmentCardService cards,
        TimeProvider timeProvider,
        ILogger<EnrollmentService> logger)
    {
        _unitOfWork = unitOfWork;
        _idempotency = idempotency;
        _promoter = promoter;
        _cards = cards;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<EnrollmentResponse> CreateAsync(
        long userId,
        long classId,
        int seats,
        string idempotencyKey,
        CancellationToken ct)
    {
        var idempotencyRequest = new IdempotencyRequest(
            userId,
            IdempotencyService.EnrollmentScope,
            idempotencyKey,
            $"{classId}:{seats}");

        try
        {
            var result = await _unitOfWork.ExecuteInTransactionAsync(
                token => CreateInTransactionAsync(idempotencyRequest, classId, seats, token),
                ct);

            await _cards.InvalidateAsync(null, [userId]);

            _logger.LogInformation(
                "Enrollment {EnrollmentId} on class {ClassId} with {Seats} seats is held by user {UserId}",
                result.Id,
                result.ClassId,
                result.Seats,
                userId);

            return result;
        }
        catch (Exception exception) when (UnitOfWork.IsUniqueViolation(
            exception,
            IdempotencyRecordConfiguration.UniqueKeyIndexName))
        {
            _logger.LogInformation(
                "Concurrent request with the same idempotency key from user {UserId} lost the race, replaying",
                userId);

            return await _idempotency.FindReplayAsync<EnrollmentResponse>(idempotencyRequest, ct)
                ?? throw new InvalidOperationException("Idempotency record disappeared after a unique violation.");
        }
    }

    public async Task<EnrollmentCardResponse> ChangeSeatsAsync(
        long userId,
        long enrollmentId,
        int seats,
        CancellationToken ct)
    {
        var affectedPassOwners = await _unitOfWork.ExecuteInTransactionAsync(
            token => ChangeSeatsInTransactionAsync(userId, enrollmentId, seats, token),
            ct);

        await _cards.InvalidateAsync(enrollmentId, affectedPassOwners);

        _logger.LogInformation(
            "Enrollment {EnrollmentId} of user {UserId} now has {Seats} seats",
            enrollmentId,
            userId,
            seats);

        return await _cards.GetAsync(enrollmentId, userId, ct);
    }

    public async Task<CancelEnrollmentResponse> CancelAsync(long userId, long enrollmentId, CancellationToken ct)
    {
        var (response, affectedPassOwners) = await _unitOfWork.ExecuteInTransactionAsync(
            token => CancelInTransactionAsync(userId, enrollmentId, token),
            ct);

        await _cards.InvalidateAsync(enrollmentId, affectedPassOwners);

        _logger.LogInformation(
            "Enrollment {EnrollmentId} of user {UserId} is cancelled, {RefundedVisits} visits refunded",
            enrollmentId,
            userId,
            response.RefundedVisits);

        return response;
    }

    private async Task<EnrollmentResponse> CreateInTransactionAsync(
        IdempotencyRequest idempotencyRequest,
        long classId,
        int seats,
        CancellationToken ct)
    {
        var lockedClass = await _unitOfWork.Class.GetForUpdateAsync(classId, ct)
            ?? throw ApiException.NotFound("Class not found.");

        _logger.LogDebug("Locked class {ClassId} to enroll user {UserId}", classId, idempotencyRequest.UserId);

        var replay = await _idempotency.FindReplayAsync<EnrollmentResponse>(idempotencyRequest, ct);

        if (replay != null)
        {
            return replay;
        }

        var now = _timeProvider.GetUtcNowTruncated();

        EnsureClassIsOpen(lockedClass, now);

        var pass = await LockPassAsync(idempotencyRequest.UserId, ct);

        EnsurePassCanBeCharged(pass, seats, now);
        await EnsureFreeSeatsAsync(lockedClass, seats, ct);

        var enrollment = new Enrollment
        {
            UserId = idempotencyRequest.UserId,
            ClassId = classId,
            Seats = seats,
            Status = EnrollmentStatus.Active,
            CreatedAt = now
        };

        await _unitOfWork.Enrollment.CreateAsync(enrollment, ct);
        pass.RemainingVisits -= seats;
        await _unitOfWork.SaveChangesAsync(ct);

        var response = new EnrollmentResponse(
            enrollment.Id,
            enrollment.ClassId,
            enrollment.Seats,
            enrollment.Status,
            enrollment.CreatedAt);

        await _idempotency.SaveAsync(idempotencyRequest, response, ct);

        return response;
    }

    private async Task<IReadOnlyList<long>> ChangeSeatsInTransactionAsync(
        long userId,
        long enrollmentId,
        int seats,
        CancellationToken ct)
    {
        var (enrollment, lockedClass) = await LockOwnedEnrollmentAsync(userId, enrollmentId, ct);
        var now = _timeProvider.GetUtcNowTruncated();

        if (enrollment.Status == EnrollmentStatus.Cancelled)
        {
            throw ApiException.Conflict(ConflictReason.EnrollmentCancelled);
        }

        EnsureClassIsOpen(lockedClass, now);

        var difference = seats - enrollment.Seats;

        _logger.LogDebug(
            "Changing enrollment {EnrollmentId} from {OldSeats} to {NewSeats} seats",
            enrollmentId,
            enrollment.Seats,
            seats);

        if (difference == 0)
        {
            return [];
        }

        var pass = await LockPassAsync(userId, ct);

        if (difference > 0)
        {
            EnsurePassCanBeCharged(pass, difference, now);
            await EnsureFreeSeatsAsync(lockedClass, difference, ct);

            enrollment.Seats = seats;
            pass.RemainingVisits -= difference;

            return [userId];
        }

        enrollment.Seats = seats;
        pass.RemainingVisits -= difference;

        // решение: уменьшение брони освобождает места так же, как отмена, поэтому тоже продвигает
        // очередь — в той же транзакции.
        var promoted = await _promoter.PromoteFirstWaitersThatFitAsync(lockedClass, now, ct);

        return [userId, .. promoted];
    }

    private async Task<(CancelEnrollmentResponse Response, IReadOnlyList<long> AffectedPassOwners)>
        CancelInTransactionAsync(long userId, long enrollmentId, CancellationToken ct)
    {
        var (enrollment, lockedClass) = await LockOwnedEnrollmentAsync(userId, enrollmentId, ct);

        if (enrollment.Status == EnrollmentStatus.Cancelled)
        {
            _logger.LogDebug("Enrollment {EnrollmentId} was already cancelled, returning stored result", enrollmentId);

            return (ToCancelResponse(enrollment), []);
        }

        var now = _timeProvider.GetUtcNowTruncated();

        if (!lockedClass.IsCancelled && now >= lockedClass.StartsAt)
        {
            throw ApiException.Conflict(ConflictReason.ClassStarted);
        }

        var refundedVisits = lockedClass.IsCancelled || lockedClass.StartsAt - now >= FullRefundNotice
            ? enrollment.Seats
            : 0;

        enrollment.Status = EnrollmentStatus.Cancelled;
        enrollment.CancelledAt = now;
        enrollment.RefundedVisits = refundedVisits;

        var affectedPassOwners = new List<long>();

        if (refundedVisits > 0)
        {
            var pass = await LockPassAsync(userId, ct);

            pass.RemainingVisits += refundedVisits;
            affectedPassOwners.Add(userId);
        }

        affectedPassOwners.AddRange(await _promoter.PromoteFirstWaitersThatFitAsync(lockedClass, now, ct));

        return (ToCancelResponse(enrollment), affectedPassOwners);
    }

    private async Task<(Enrollment Enrollment, Class LockedClass)> LockOwnedEnrollmentAsync(
        long userId,
        long enrollmentId,
        CancellationToken ct)
    {
        var enrollment = await _unitOfWork.Enrollment.GetOwnedAsync(enrollmentId, userId, ct)
            ?? throw ApiException.NotFound("Enrollment not found.");

        var lockedClass = await _unitOfWork.Class.GetForUpdateAsync(enrollment.ClassId, ct)
            ?? throw ApiException.NotFound("Class not found.");

        _logger.LogDebug("Locked class {ClassId} for enrollment {EnrollmentId}", lockedClass.Id, enrollmentId);

        await _unitOfWork.DbContext.Entry(enrollment).ReloadAsync(ct);

        return (enrollment, lockedClass);
    }

    private async Task<Pass> LockPassAsync(long userId, CancellationToken ct)
    {
        return await _unitOfWork.Pass.GetByUserForUpdateAsync(userId, ct)
            ?? throw new InvalidOperationException($"User {userId} has no pass.");
    }

    private async Task EnsureFreeSeatsAsync(Class lockedClass, int requestedSeats, CancellationToken ct)
    {
        var bookedSeats = await _unitOfWork.Enrollment.SumActiveSeatsAsync(lockedClass.Id, ct);

        if (bookedSeats + requestedSeats > lockedClass.Capacity)
        {
            throw ApiException.Conflict(ConflictReason.CapacityExceeded);
        }
    }

    private static void EnsureClassIsOpen(Class lockedClass, DateTime now)
    {
        if (lockedClass.IsCancelled)
        {
            throw ApiException.Conflict(ConflictReason.ClassCancelled);
        }

        if (now >= lockedClass.StartsAt)
        {
            throw ApiException.Conflict(ConflictReason.ClassStarted);
        }
    }

    private static void EnsurePassCanBeCharged(Pass pass, int visits, DateTime now)
    {
        if (pass.ValidUntil < DateOnly.FromDateTime(now))
        {
            throw ApiException.Conflict(ConflictReason.PassExpired);
        }

        if (pass.RemainingVisits < visits)
        {
            throw ApiException.Conflict(ConflictReason.PassExhausted);
        }
    }

    private static CancelEnrollmentResponse ToCancelResponse(Enrollment enrollment)
    {
        return new CancelEnrollmentResponse(enrollment.Id, enrollment.Status, enrollment.RefundedVisits);
    }
}
