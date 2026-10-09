using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Models;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Repositories;

[TestClass]
public class RepositoryTests : ApiTestBase
{
    [TestMethod]
    public async Task ClassGetForUpdate_ReturnsCancelledClassesToo()
    {
        var cancelled = await TestData.CreateClassAsync(cancelled: true);

        var loaded = await WithUnitOfWorkAsync(uow => uow.ExecuteInTransactionAsync(
            ct => uow.Class.GetForUpdateAsync(cancelled.Id, ct)));

        Assert.IsNotNull(loaded);
        Assert.IsTrue(loaded.IsCancelled);
    }

    [TestMethod]
    public async Task ClassGetForUpdate_ReturnsNullForUnknownId()
    {
        var loaded = await WithUnitOfWorkAsync(uow => uow.ExecuteInTransactionAsync(
            ct => uow.Class.GetForUpdateAsync(long.MaxValue, ct)));

        Assert.IsNull(loaded);
    }

    [TestMethod]
    public async Task ClassGetForUpdate_BlocksSecondLockerUntilCommit()
    {
        var scheduledClass = await TestData.CreateClassAsync();
        var firstLocked = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var order = new List<string>();

        var first = WithUnitOfWorkAsync(uow => uow.ExecuteInTransactionAsync(async ct =>
        {
            await uow.Class.GetForUpdateAsync(scheduledClass.Id, ct);
            firstLocked.SetResult();
            await releaseFirst.Task;
            lock (order)
            {
                order.Add("first-commit");
            }

            return 0;
        }));

        await firstLocked.Task;

        var second = WithUnitOfWorkAsync(uow => uow.ExecuteInTransactionAsync(async ct =>
        {
            await uow.Class.GetForUpdateAsync(scheduledClass.Id, ct);
            lock (order)
            {
                order.Add("second-locked");
            }

            return 0;
        }));

        await Task.Delay(300);
        Assert.IsFalse(second.IsCompleted);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        CollectionAssert.AreEqual(new[] { "first-commit", "second-locked" }, order);
    }

    [TestMethod]
    public async Task EnrollmentSumActiveSeats_CountsOnlyActive()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 20);
        var user = await TestData.CreateUserAsync();
        await TestData.CreateEnrollmentAsync(user.Id, scheduledClass.Id, 3);
        await TestData.CreateEnrollmentAsync(user.Id, scheduledClass.Id, 4);
        await TestData.CreateEnrollmentAsync(user.Id, scheduledClass.Id, 5, EnrollmentStatus.Cancelled);

        var sum = await WithUnitOfWorkAsync(uow => uow.Enrollment.SumActiveSeatsAsync(scheduledClass.Id));

        Assert.AreEqual(7, sum);
    }

    [TestMethod]
    public async Task EnrollmentSumActiveSeats_IsZeroForEmptyClass()
    {
        var scheduledClass = await TestData.CreateClassAsync();

        Assert.AreEqual(0, await WithUnitOfWorkAsync(uow => uow.Enrollment.SumActiveSeatsAsync(scheduledClass.Id)));
    }

    [TestMethod]
    public async Task EnrollmentGetOwned_FiltersByOwnerAndSeesCancelledClasses()
    {
        var owner = await TestData.CreateUserAsync();
        var stranger = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(cancelled: true);
        var enrollment = await TestData.CreateEnrollmentAsync(owner.Id, scheduledClass.Id, 1);

        var own = await WithUnitOfWorkAsync(uow => uow.Enrollment.GetOwnedAsync(enrollment.Id, owner.Id));
        var foreign = await WithUnitOfWorkAsync(uow => uow.Enrollment.GetOwnedAsync(enrollment.Id, stranger.Id));

        Assert.IsNotNull(own);
        Assert.IsNull(foreign);
    }

    [TestMethod]
    public async Task EnrollmentGetWithClassAndRoom_IncludesCancelledClassAndRoom()
    {
        var owner = await TestData.CreateUserAsync();
        var room = await TestData.CreateRoomAsync();
        var scheduledClass = await TestData.CreateClassAsync(cancelled: true, roomId: room.Id);
        var enrollment = await TestData.CreateEnrollmentAsync(owner.Id, scheduledClass.Id, 1);

        var loaded = await WithUnitOfWorkAsync(uow => uow.Enrollment.GetWithClassAndRoomAsync(enrollment.Id));

        Assert.IsNotNull(loaded);
        Assert.IsTrue(loaded.Class.IsCancelled);
        Assert.AreEqual(room.Name, loaded.Class.Room.Name);
    }

    [TestMethod]
    public async Task PassGetByUserForUpdate_ReturnsUsersPass()
    {
        var user = await TestData.CreateUserAsync(visits: 7);

        var pass = await WithUnitOfWorkAsync(uow => uow.ExecuteInTransactionAsync(
            ct => uow.Pass.GetByUserForUpdateAsync(user.Id, ct)));

        Assert.AreEqual(7, pass!.RemainingVisits);
    }

    [TestMethod]
    public async Task WaitlistQueries_RespectStatusAndOrder()
    {
        var scheduledClass = await TestData.CreateClassAsync();
        var users = new List<TestUser>();

        for (var i = 0; i < 4; i++)
        {
            users.Add(await TestData.CreateUserAsync());
        }

        var entries = await TestApp.WithDbAsync(async db =>
        {
            var created = new List<WaitlistEntry>
            {
                NewEntry(users[0].Id, scheduledClass.Id, Now.AddMinutes(3), WaitlistStatus.Waiting),
                NewEntry(users[1].Id, scheduledClass.Id, Now.AddMinutes(1), WaitlistStatus.Waiting),
                NewEntry(users[2].Id, scheduledClass.Id, Now.AddMinutes(2), WaitlistStatus.Cancelled),
                NewEntry(users[3].Id, scheduledClass.Id, Now.AddMinutes(1), WaitlistStatus.Waiting)
            };

            db.WaitlistEntries.AddRange(created);
            await db.SaveChangesAsync();

            return created;
        });

        await WithUnitOfWorkAsync(async uow =>
        {
            var waiting = await uow.Waitlist.GetWaitingInQueueOrderAsync(scheduledClass.Id);

            CollectionAssert.AreEqual(
                new[] { entries[1].Id, entries[3].Id, entries[0].Id },
                waiting.Select(x => x.Id).ToArray());

            Assert.AreEqual(1, await uow.Waitlist.GetPositionAsync(entries[1]));
            Assert.AreEqual(2, await uow.Waitlist.GetPositionAsync(entries[3]));
            Assert.AreEqual(3, await uow.Waitlist.GetPositionAsync(entries[0]));
            Assert.IsTrue(await uow.Waitlist.IsWaitingAsync(users[0].Id, scheduledClass.Id));
            Assert.IsFalse(await uow.Waitlist.IsWaitingAsync(users[2].Id, scheduledClass.Id));
            Assert.IsNotNull(await uow.Waitlist.GetOwnedAsync(entries[2].Id, users[2].Id));
            Assert.IsNull(await uow.Waitlist.GetOwnedAsync(entries[2].Id, users[0].Id));

            return 0;
        });
    }

    [TestMethod]
    public async Task Waitlist_DatabaseAllowsOnlyOneWaitingEntryPerUserAndClass()
    {
        var scheduledClass = await TestData.CreateClassAsync();
        var user = await TestData.CreateUserAsync();

        await TestApp.WithDbAsync(async db =>
        {
            db.WaitlistEntries.Add(NewEntry(user.Id, scheduledClass.Id, Now, WaitlistStatus.Cancelled));
            db.WaitlistEntries.Add(NewEntry(user.Id, scheduledClass.Id, Now, WaitlistStatus.Waiting));
            await db.SaveChangesAsync();
        });

        await Assert.ThrowsExceptionAsync<DbUpdateException>(() => TestApp.WithDbAsync(async db =>
        {
            db.WaitlistEntries.Add(NewEntry(user.Id, scheduledClass.Id, Now, WaitlistStatus.Waiting));
            await db.SaveChangesAsync();
        }));
    }

    [TestMethod]
    public async Task Pass_DatabaseRejectsNegativeVisits()
    {
        var user = await TestData.CreateUserAsync(visits: 1);

        await Assert.ThrowsExceptionAsync<DbUpdateException>(() => TestData.SetPassAsync(user.Id, visits: -1));
    }

    [TestMethod]
    public async Task Enrollment_DatabaseRejectsNonPositiveSeats()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();

        await Assert.ThrowsExceptionAsync<DbUpdateException>(() => TestData.CreateEnrollmentAsync(user.Id, scheduledClass.Id, 0));
    }

    [TestMethod]
    public async Task IdempotencyRecordFind_MatchesUserScopeAndKey()
    {
        var user = await TestData.CreateUserAsync();
        var other = await TestData.CreateUserAsync();

        await TestApp.WithDbAsync(async db =>
        {
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                UserId = user.Id,
                Scope = "enrollments",
                KeyHash = "HASH",
                RequestHash = "REQ",
                ResponseBody = "{}",
                CreatedAt = Now
            });

            await db.SaveChangesAsync();
        });

        await WithUnitOfWorkAsync(async uow =>
        {
            Assert.IsNotNull(await uow.IdempotencyRecord.FindAsync(user.Id, "enrollments", "HASH"));
            Assert.IsNull(await uow.IdempotencyRecord.FindAsync(other.Id, "enrollments", "HASH"));
            Assert.IsNull(await uow.IdempotencyRecord.FindAsync(user.Id, "enrollments/waitlist", "HASH"));
            Assert.IsNull(await uow.IdempotencyRecord.FindAsync(user.Id, "enrollments", "OTHER"));

            return 0;
        });
    }

    [TestMethod]
    public async Task ClassGetSchedule_PagesWithCursorAndComputesSeats()
    {
        var room = await TestData.CreateRoomAsync();
        var user = await TestData.CreateUserAsync();
        var start = new DateTime(2038, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var a = await TestData.CreateClassAsync(capacity: 5, startsAt: start, roomId: room.Id);
        var b = await TestData.CreateClassAsync(capacity: 5, startsAt: start, roomId: room.Id);
        var c = await TestData.CreateClassAsync(capacity: 5, startsAt: start.AddHours(1), roomId: room.Id);
        await TestData.CreateClassAsync(capacity: 5, startsAt: start.AddHours(2), roomId: room.Id, cancelled: true);
        await TestData.CreateEnrollmentAsync(user.Id, a.Id, 2);
        await TestData.FillClassAsync(a.Id, 1);

        var firstPage = await WithUnitOfWorkAsync(uow => uow.Class.GetScheduleAsync(
            start.AddDays(-1), start.AddDays(1), room.Id, null, 2, user.Id));
        var secondPage = await WithUnitOfWorkAsync(uow => uow.Class.GetScheduleAsync(
            start.AddDays(-1), start.AddDays(1), room.Id, new ScheduleCursor(firstPage[^1].StartsAt, firstPage[^1].ClassId), 2, user.Id));

        CollectionAssert.AreEqual(new[] { a.Id, b.Id }, firstPage.Select(x => x.ClassId).ToArray());
        CollectionAssert.AreEqual(new[] { c.Id }, secondPage.Select(x => x.ClassId).ToArray());
        Assert.AreEqual(3, firstPage[0].BookedSeats);
        Assert.AreEqual(2, firstPage[0].MySeats);
        Assert.AreEqual(0, firstPage[1].BookedSeats);
    }

    [TestMethod]
    public async Task ClassGetRoomLoad_GroupsByUtcDay()
    {
        var room = await TestData.CreateRoomAsync();
        var day = new DateTime(2038, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var first = await TestData.CreateClassAsync(capacity: 4, startsAt: day.AddHours(1), roomId: room.Id);
        await TestData.CreateClassAsync(capacity: 6, startsAt: day.AddHours(23), roomId: room.Id);
        await TestData.CreateClassAsync(capacity: 9, startsAt: day.AddDays(1), roomId: room.Id);
        await TestData.FillClassAsync(first.Id, 3);

        var rows = await WithUnitOfWorkAsync(uow => uow.Class.GetRoomLoadAsync(
            new DateOnly(2038, 2, 1),
            new DateOnly(2038, 2, 2),
            room.Id));

        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(new DateOnly(2038, 2, 1), rows[0].Date);
        Assert.AreEqual(2, rows[0].ClassesCount);
        Assert.AreEqual(10, rows[0].TotalCapacity);
        Assert.AreEqual(3, rows[0].BookedSeats);
        Assert.AreEqual(new DateOnly(2038, 2, 2), rows[1].Date);
        Assert.AreEqual(9, rows[1].TotalCapacity);
    }

    [TestMethod]
    public async Task ClassGetRoomLoad_HandlesExtremeDates()
    {
        var rows = await WithUnitOfWorkAsync(uow => uow.Class.GetRoomLoadAsync(
            DateOnly.MinValue,
            DateOnly.MaxValue,
            long.MaxValue));

        Assert.AreEqual(0, rows.Count);
    }

    [TestMethod]
    public async Task ExecuteInTransaction_RollsBackOnException()
    {
        var user = await TestData.CreateUserAsync(visits: 5);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => WithUnitOfWorkAsync(uow =>
            uow.ExecuteInTransactionAsync<int>(async ct =>
            {
                var pass = await uow.Pass.GetByUserForUpdateAsync(user.Id, ct);
                pass!.RemainingVisits = 0;
                await uow.SaveChangesAsync(ct);

                throw new InvalidOperationException("boom");
            })));

        Assert.AreEqual(5, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public void Migration_RefundedVisitsHasDefaultForOldRows()
    {
        using var scope = TestApp.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var property = db.Model.FindEntityType(typeof(Enrollment))!.FindProperty(nameof(Enrollment.RefundedVisits))!;

        Assert.AreEqual(0, property.GetDefaultValue());
    }

    [TestMethod]
    public async Task Seed_CancelledEnrollmentsHaveRefundMatchingRule()
    {
        var mismatches = await TestApp.WithDbAsync(db => db.Enrollments
            .IgnoreQueryFilters()
            .Where(x => x.Status == EnrollmentStatus.Cancelled && x.CancelledAt != null && x.UserId <= 205)
            .Select(x => new
            {
                x.Seats,
                x.RefundedVisits,
                Expected = x.Class.IsCancelled || x.CancelledAt <= x.Class.StartsAt.AddHours(-2) ? x.Seats : 0
            })
            .Where(x => x.RefundedVisits != x.Expected)
            .CountAsync());

        Assert.AreEqual(0, mismatches);
    }

    private static WaitlistEntry NewEntry(long userId, long classId, DateTime createdAt, string status)
    {
        return new WaitlistEntry
        {
            UserId = userId,
            ClassId = classId,
            Seats = 1,
            Status = status,
            CreatedAt = createdAt
        };
    }

    private static async Task<T> WithUnitOfWorkAsync<T>(Func<IUnitOfWork, Task<T>> action)
    {
        await using var scope = TestApp.Services.CreateAsyncScope();

        return await action(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }
}
