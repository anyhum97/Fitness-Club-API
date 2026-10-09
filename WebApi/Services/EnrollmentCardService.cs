using Common.Caching;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using WebApi.Errors;
using WebApi.Models;

namespace WebApi.Services;

public class EnrollmentCardService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cache;
    private readonly ILogger<EnrollmentCardService> _logger;

    public EnrollmentCardService(IUnitOfWork unitOfWork, ICacheService cache, ILogger<EnrollmentCardService> logger)
    {
        _unitOfWork = unitOfWork;
        _cache = cache;
        _logger = logger;
    }

    public static string EnrollmentKey(long enrollmentId)
    {
        return $"enrollment-card:{enrollmentId}";
    }

    public static string PassKey(long userId)
    {
        return $"pass:{userId}";
    }

    public async Task<EnrollmentCardResponse> GetAsync(long enrollmentId, long userId, CancellationToken ct)
    {
        var enrollment = await GetEnrollmentAsync(enrollmentId, ct);

        if (enrollment == null || enrollment.UserId != userId)
        {
            throw ApiException.NotFound("Enrollment not found.");
        }

        var pass = await GetPassAsync(userId, ct);

        return new EnrollmentCardResponse(
            enrollment.Id,
            enrollment.Status,
            enrollment.Seats,
            enrollment.CreatedAt,
            enrollment.Class,
            pass);
    }

    public async Task InvalidateAsync(long? enrollmentId, IEnumerable<long> passOwnerIds)
    {
        if (enrollmentId.HasValue)
        {
            await _cache.RemoveAsync(EnrollmentKey(enrollmentId.Value));
        }

        var userIds = passOwnerIds.Distinct().ToList();

        foreach (var userId in userIds)
        {
            await _cache.RemoveAsync(PassKey(userId));
        }

        _logger.LogDebug(
            "Cache invalidated: enrollment {EnrollmentId}, passes of users {UserIds}",
            enrollmentId,
            userIds);
    }

    private async Task<CachedEnrollment?> GetEnrollmentAsync(long enrollmentId, CancellationToken ct)
    {
        var key = EnrollmentKey(enrollmentId);
        var cached = await _cache.GetAsync<CachedEnrollment>(key, ct);

        if (cached != null)
        {
            _logger.LogDebug("Enrollment card {EnrollmentId} served from cache", enrollmentId);

            return cached;
        }

        _logger.LogDebug("Enrollment card {EnrollmentId} not in cache, loading from database", enrollmentId);

        var enrollment = await _unitOfWork.Enrollment.GetWithClassAndRoomAsync(enrollmentId, ct);

        if (enrollment == null)
        {
            return null;
        }

        cached = new CachedEnrollment(
            enrollment.Id,
            enrollment.UserId,
            enrollment.Status,
            enrollment.Seats,
            enrollment.CreatedAt,
            new EnrollmentClassResponse(
                enrollment.Class.Id,
                enrollment.Class.Title,
                enrollment.Class.StartsAt,
                enrollment.Class.DurationMinutes,
                enrollment.Class.IsCancelled,
                new RoomResponse(enrollment.Class.Room.Id, enrollment.Class.Room.Name)));

        await _cache.SetAsync(key, cached, ct: ct);

        return cached;
    }

    private async Task<PassResponse> GetPassAsync(long userId, CancellationToken ct)
    {
        var key = PassKey(userId);
        var cached = await _cache.GetAsync<PassResponse>(key, ct);

        if (cached != null)
        {
            return cached;
        }

        var pass = await _unitOfWork.Pass
            .Query()
            .AsNoTracking()
            .FirstAsync(x => x.UserId == userId, ct);

        cached = new PassResponse(pass.RemainingVisits, pass.ValidUntil);

        await _cache.SetAsync(key, cached, ct: ct);

        return cached;
    }

    private record CachedEnrollment(
        long Id,
        long UserId,
        string Status,
        int Seats,
        DateTime CreatedAt,
        EnrollmentClassResponse Class);
}
