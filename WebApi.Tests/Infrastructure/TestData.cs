using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Auth;

namespace WebApi.Tests.Infrastructure;

public record TestUser(User User, ApiClient Client)
{
    public long Id => User.Id;
}

public static class TestData
{
    public static async Task<Room> CreateRoomAsync()
    {
        var room = new Room { Name = $"Room {Guid.NewGuid():N}" };

        await TestApp.WithDbAsync(async db =>
        {
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
        });

        return room;
    }

    public static async Task<Class> CreateClassAsync(
        int capacity = 10,
        TimeSpan? startsIn = null,
        DateTime? startsAt = null,
        bool cancelled = false,
        long? roomId = null,
        int durationMinutes = 60,
        string? title = null)
    {
        var scheduledClass = new Class
        {
            RoomId = roomId ?? (await CreateRoomAsync()).Id,
            Title = title ?? "Test class",
            StartsAt = startsAt ?? TestApp.Clock.UtcNow + (startsIn ?? TimeSpan.FromDays(1)),
            DurationMinutes = durationMinutes,
            Capacity = capacity,
            IsCancelled = cancelled
        };

        await TestApp.WithDbAsync(async db =>
        {
            db.Classes.Add(scheduledClass);
            await db.SaveChangesAsync();
        });

        return scheduledClass;
    }

    public static async Task<TestUser> CreateUserAsync(
        int visits = 20,
        DateOnly? validUntil = null,
        string role = "user")
    {
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@club.test",
            PasswordHash = "not-used",
            Role = role,
            CreatedAt = DateTime.UtcNow
        };

        await TestApp.WithDbAsync(async db =>
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();

            db.Passes.Add(new Pass
            {
                UserId = user.Id,
                RemainingVisits = visits,
                ValidUntil = validUntil ?? TestApp.Clock.Today.AddDays(30)
            });

            await db.SaveChangesAsync();
        });

        return new TestUser(user, CreateClient(user));
    }

    public static ApiClient CreateClient(User user)
    {
        var token = TestApp.Services.GetRequiredService<AccessTokenService>().Create(user).AccessToken;

        return ApiClient.WithToken(token);
    }

    public static async Task<Enrollment> CreateEnrollmentAsync(
        long userId,
        long classId,
        int seats,
        string status = EnrollmentStatus.Active,
        int refundedVisits = 0)
    {
        var enrollment = new Enrollment
        {
            UserId = userId,
            ClassId = classId,
            Seats = seats,
            Status = status,
            CreatedAt = TestApp.Clock.UtcNow.AddDays(-1),
            CancelledAt = status == EnrollmentStatus.Cancelled ? TestApp.Clock.UtcNow.AddHours(-1) : null,
            RefundedVisits = refundedVisits
        };

        await TestApp.WithDbAsync(async db =>
        {
            db.Enrollments.Add(enrollment);
            await db.SaveChangesAsync();
        });

        return enrollment;
    }

    public static async Task FillClassAsync(long classId, int seats)
    {
        var filler = await CreateUserAsync();

        await CreateEnrollmentAsync(filler.Id, classId, seats);
    }

    public static Task<Pass> GetPassAsync(long userId)
    {
        return TestApp.WithDbAsync(db => db.Passes.AsNoTracking().SingleAsync(x => x.UserId == userId));
    }

    public static Task SetPassAsync(long userId, int? visits = null, DateOnly? validUntil = null)
    {
        return TestApp.WithDbAsync(async db =>
        {
            var pass = await db.Passes.SingleAsync(x => x.UserId == userId);

            pass.RemainingVisits = visits ?? pass.RemainingVisits;
            pass.ValidUntil = validUntil ?? pass.ValidUntil;

            await db.SaveChangesAsync();
        });
    }

    public static Task SetClassAsync(long classId, bool? cancelled = null, int? capacity = null)
    {
        return TestApp.WithDbAsync(async db =>
        {
            var scheduledClass = await db.Classes.IgnoreQueryFilters().SingleAsync(x => x.Id == classId);

            scheduledClass.IsCancelled = cancelled ?? scheduledClass.IsCancelled;
            scheduledClass.Capacity = capacity ?? scheduledClass.Capacity;

            await db.SaveChangesAsync();
        });
    }

    public static Task<Enrollment> GetEnrollmentAsync(long id)
    {
        return TestApp.WithDbAsync(db => db.Enrollments.AsNoTracking().SingleAsync(x => x.Id == id));
    }

    public static Task<List<Enrollment>> GetEnrollmentsAsync(long classId)
    {
        return TestApp.WithDbAsync(db => db.Enrollments
            .AsNoTracking()
            .Where(x => x.ClassId == classId)
            .OrderBy(x => x.Id)
            .ToListAsync());
    }

    public static Task<int> GetActiveSeatsAsync(long classId)
    {
        return TestApp.WithDbAsync(db => db.Enrollments
            .Where(x => x.ClassId == classId && x.Status == EnrollmentStatus.Active)
            .SumAsync(x => x.Seats));
    }

    public static Task<WaitlistEntry> GetWaitlistEntryAsync(long id)
    {
        return TestApp.WithDbAsync(db => db.WaitlistEntries.AsNoTracking().SingleAsync(x => x.Id == id));
    }

    public static Task<List<WaitlistEntry>> GetWaitlistAsync(long classId)
    {
        return TestApp.WithDbAsync(db => db.WaitlistEntries
            .AsNoTracking()
            .Where(x => x.ClassId == classId)
            .OrderBy(x => x.Id)
            .ToListAsync());
    }

    public static Task<int> CountIdempotencyRecordsAsync(long userId)
    {
        return TestApp.WithDbAsync(db => db.IdempotencyRecords.CountAsync(x => x.UserId == userId));
    }
}
