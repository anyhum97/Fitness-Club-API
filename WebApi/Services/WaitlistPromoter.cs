using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;

namespace WebApi.Services;

public class WaitlistPromoter
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<WaitlistPromoter> _logger;

    public WaitlistPromoter(IUnitOfWork unitOfWork, ILogger<WaitlistPromoter> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<IReadOnlyList<long>> PromoteFirstWaitersThatFitAsync(
        Class lockedClass,
        DateTime now,
        CancellationToken ct)
    {
        var promotedUserIds = new List<long>();

        if (lockedClass.IsCancelled || now >= lockedClass.StartsAt)
        {
            return promotedUserIds;
        }

        await _unitOfWork.SaveChangesAsync(ct);

        var freeSeats = lockedClass.Capacity - await _unitOfWork.Enrollment.SumActiveSeatsAsync(lockedClass.Id, ct);
        var today = DateOnly.FromDateTime(now);

        if (freeSeats <= 0)
        {
            return promotedUserIds;
        }

        var queue = await _unitOfWork.Waitlist.GetWaitingInQueueOrderAsync(lockedClass.Id, ct);

        _logger.LogDebug(
            "Scanning waitlist of class {ClassId}: {WaitingCount} waiting, {FreeSeats} free seats",
            lockedClass.Id,
            queue.Count,
            freeSeats);

        foreach (var entry in queue)
        {
            // решение: тот, кому освободившихся мест не хватает, не задерживает очередь — места получает
            // следующий, кому хватает, а он остаётся на своей позиции.
            if (entry.Seats > freeSeats)
            {
                _logger.LogDebug(
                    "Waitlist entry {EntryId} needs {Seats} seats, only {FreeSeats} free, keeps its position",
                    entry.Id,
                    entry.Seats,
                    freeSeats);

                continue;
            }

            var pass = await _unitOfWork.Pass.GetByUserForUpdateAsync(entry.UserId, ct);

            // решение: ожидающий с истёкшим абонементом или без нужного числа посещений пропускается
            // и остаётся в очереди; отдельной причины отказа для него нет.
            if (pass == null || !CanCharge(pass, entry.Seats, today))
            {
                _logger.LogWarning(
                    "Waitlist entry {EntryId} of user {UserId} skipped: pass cannot cover {Seats} visits "
                    + "(remaining {RemainingVisits}, valid until {ValidUntil})",
                    entry.Id,
                    entry.UserId,
                    entry.Seats,
                    pass?.RemainingVisits,
                    pass?.ValidUntil);

                continue;
            }

            var enrollment = new Enrollment
            {
                UserId = entry.UserId,
                ClassId = lockedClass.Id,
                Seats = entry.Seats,
                Status = EnrollmentStatus.Active,
                CreatedAt = now
            };

            await _unitOfWork.Enrollment.CreateAsync(enrollment, ct);

            pass.RemainingVisits -= entry.Seats;
            entry.Status = WaitlistStatus.Promoted;
            entry.PromotedAt = now;

            await _unitOfWork.SaveChangesAsync(ct);

            entry.EnrollmentId = enrollment.Id;
            freeSeats -= entry.Seats;

            _logger.LogInformation(
                "Waitlist entry {EntryId} of user {UserId} promoted to enrollment {EnrollmentId} on class {ClassId}, "
                + "{Seats} visits charged",
                entry.Id,
                entry.UserId,
                enrollment.Id,
                lockedClass.Id,
                entry.Seats);
            promotedUserIds.Add(entry.UserId);

            if (freeSeats == 0)
            {
                break;
            }
        }

        await _unitOfWork.SaveChangesAsync(ct);

        return promotedUserIds;
    }

    private static bool CanCharge(Pass pass, int visits, DateOnly today)
    {
        return pass.ValidUntil >= today && pass.RemainingVisits >= visits;
    }
}
