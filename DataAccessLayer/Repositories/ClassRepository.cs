using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using DataAccessLayer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DataAccessLayer.Repositories;

public class ClassRepository : GenericRepository<Class>, IClassRepository
{
    public ClassRepository(DbSet<Class> dbSet)
        : base(dbSet)
    {
    }

    public async Task<Class?> GetWithEnrollmentsAsync(long id, CancellationToken ct = default)
    {
        return await _dbSet
            .Include(x => x.Enrollments)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<Class?> GetForUpdateAsync(long id, CancellationToken ct = default)
    {
        var classes = await _dbSet
            .FromSql($"SELECT * FROM classes WHERE id = {id} FOR UPDATE")
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        return classes.SingleOrDefault();
    }

    public async Task<List<ScheduleRow>> GetScheduleAsync(
        DateTime from,
        DateTime to,
        long? roomId,
        ScheduleCursor? after,
        int take,
        long userId,
        CancellationToken ct = default)
    {
        var query = _dbSet
            .AsNoTracking()
            .Where(x => !x.IsCancelled && x.StartsAt >= from && x.StartsAt < to);

        if (roomId.HasValue)
        {
            query = query.Where(x => x.RoomId == roomId.Value);
        }

        if (after != null)
        {
            query = query.Where(x =>
                x.StartsAt > after.StartsAt || (x.StartsAt == after.StartsAt && x.Id > after.ClassId));
        }

        return await query
            .OrderBy(x => x.StartsAt)
            .ThenBy(x => x.Id)
            .Take(take)
            .Select(x => new ScheduleRow
            {
                ClassId = x.Id,
                Title = x.Title,
                StartsAt = x.StartsAt,
                DurationMinutes = x.DurationMinutes,
                RoomId = x.RoomId,
                RoomName = x.Room.Name,
                Capacity = x.Capacity,
                BookedSeats = x.Enrollments
                    .Where(e => e.Status == EnrollmentStatus.Active)
                    .Sum(e => e.Seats),
                MySeats = x.Enrollments
                    .Where(e => e.Status == EnrollmentStatus.Active && e.UserId == userId)
                    .Sum(e => e.Seats)
            })
            .ToListAsync(ct);
    }

    public async Task<List<RoomLoadRow>> GetRoomLoadAsync(
        DateOnly from,
        DateOnly to,
        long? roomId,
        CancellationToken ct = default)
    {
        var fromMoment = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toMoment = to == DateOnly.MaxValue
            ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
            : to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var filterByRoom = roomId.HasValue;
        var roomIdValue = roomId ?? 0;

        var database = _dbSet.GetService<ICurrentDbContext>().Context.Database;

        return await database
            .SqlQuery<RoomLoadRow>($"""
                SELECT
                    (c.starts_at AT TIME ZONE 'UTC')::date AS "Date",
                    c.room_id AS "RoomId",
                    r.name AS "RoomName",
                    count(*)::int AS "ClassesCount",
                    sum(c.capacity)::int AS "TotalCapacity",
                    coalesce(sum(b.booked), 0)::int AS "BookedSeats"
                FROM classes c
                JOIN rooms r ON r.id = c.room_id
                LEFT JOIN LATERAL (
                    SELECT sum(e.seats) AS booked
                    FROM enrollments e
                    WHERE e.class_id = c.id AND e.status = 'active'
                ) b ON true
                WHERE c.is_cancelled = false
                    AND c.starts_at >= {fromMoment}
                    AND c.starts_at < {toMoment}
                    AND ({filterByRoom} = false OR c.room_id = {roomIdValue})
                GROUP BY 1, 2, 3
                ORDER BY 1, 2
                """)
            .ToListAsync(ct);
    }
}
