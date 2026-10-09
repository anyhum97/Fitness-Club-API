using System.Net;
using DataAccessLayer.Entities;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Scenarios;

[TestClass]
public class ConcurrencyTests : ApiTestBase
{
    [TestMethod]
    public async Task ParallelEnrollments_NeverExceedCapacity()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 5);
        var users = await CreateUsersAsync(20, visits: 5);

        var responses = await Task.WhenAll(users.Select(user => user.Client.EnrollAsync(scheduledClass.Id, 1)));

        Assert.AreEqual(5, responses.Count(x => x.StatusCode == HttpStatusCode.Created));

        foreach (var response in responses.Where(x => x.StatusCode != HttpStatusCode.Created))
        {
            await ResponseAssert.ConflictAsync(response, "capacity-exceeded");
        }

        Assert.AreEqual(5, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    public async Task ParallelEnrollmentsWithMixedSizes_FillExactlyWithoutOverbooking()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 7);
        var users = await CreateUsersAsync(15, visits: 10);

        var responses = await Task.WhenAll(
            users.Select((user, index) => user.Client.EnrollAsync(scheduledClass.Id, 1 + index % 3)));

        var booked = await TestData.GetActiveSeatsAsync(scheduledClass.Id);
        Assert.IsTrue(booked <= 7, $"booked {booked}");
        Assert.IsTrue(booked >= 5, $"booked {booked}");

        for (var i = 0; i < users.Count; i++)
        {
            var expectedVisits = responses[i].StatusCode == HttpStatusCode.Created ? 10 - (1 + i % 3) : 10;
            Assert.AreEqual(expectedVisits, (await TestData.GetPassAsync(users[i].Id)).RemainingVisits);
        }
    }

    [TestMethod]
    public async Task ParallelEnrollmentsBySameUser_NeverDriveVisitsBelowZero()
    {
        var user = await TestData.CreateUserAsync(visits: 3);
        var classes = new List<long>();

        for (var i = 0; i < 10; i++)
        {
            classes.Add((await TestData.CreateClassAsync()).Id);
        }

        var responses = await Task.WhenAll(classes.Select(classId => user.Client.EnrollAsync(classId, 1)));

        Assert.AreEqual(3, responses.Count(x => x.StatusCode == HttpStatusCode.Created));

        foreach (var response in responses.Where(x => x.StatusCode != HttpStatusCode.Created))
        {
            await ResponseAssert.ConflictAsync(response, "pass-exhausted");
        }

        Assert.AreEqual(0, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task ParallelSeatIncreases_NeverExceedCapacityOrVisits()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 6);
        var users = await CreateUsersAsync(3, visits: 4);
        var enrollments = new List<long>();

        foreach (var user in users)
        {
            enrollments.Add(await user.EnrollAsync(scheduledClass.Id, 1));
        }

        var responses = await Task.WhenAll(
            users.Select((user, index) => user.Client.ChangeSeatsAsync(enrollments[index], 4)));

        Assert.AreEqual(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.IsTrue(await TestData.GetActiveSeatsAsync(scheduledClass.Id) <= 6);

        foreach (var user in users)
        {
            Assert.IsTrue((await TestData.GetPassAsync(user.Id)).RemainingVisits >= 0);
        }
    }

    [TestMethod]
    public async Task ParallelCancelAndEnroll_DoNotLoseOrDuplicateSeats()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 4);
        var holders = await CreateUsersAsync(4, visits: 1);
        var holderEnrollments = new List<long>();

        foreach (var holder in holders)
        {
            holderEnrollments.Add(await holder.EnrollAsync(scheduledClass.Id, 1));
        }

        var newcomers = await CreateUsersAsync(8, visits: 1);

        var cancels = holders.Select((holder, index) => holder.Client.CancelAsync(holderEnrollments[index]));
        var enrolls = newcomers.Select(user => user.Client.EnrollAsync(scheduledClass.Id, 1));
        var responses = await Task.WhenAll(cancels.Concat(enrolls));

        Assert.IsTrue(responses.Take(4).All(x => x.StatusCode == HttpStatusCode.OK));
        var created = responses.Skip(4).Count(x => x.StatusCode == HttpStatusCode.Created);
        Assert.AreEqual(created, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
        Assert.IsTrue(created <= 4);
    }

    [TestMethod]
    public async Task ParallelCancellations_PromoteEachWaiterExactlyOnce()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 5);
        var holders = await CreateUsersAsync(5, visits: 1);
        var holderEnrollments = new List<long>();

        foreach (var holder in holders)
        {
            holderEnrollments.Add(await holder.EnrollAsync(scheduledClass.Id, 1));
        }

        var waiters = await CreateUsersAsync(7, visits: 1);
        var entryIds = new List<long>();

        foreach (var waiter in waiters)
        {
            entryIds.Add(await waiter.JoinWaitlistAsync(scheduledClass.Id, 1));
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var responses = await Task.WhenAll(
            holders.Select((holder, index) => holder.Client.CancelAsync(holderEnrollments[index])));

        Assert.IsTrue(responses.All(x => x.StatusCode == HttpStatusCode.OK));
        Assert.AreEqual(5, await TestData.GetActiveSeatsAsync(scheduledClass.Id));

        var statuses = new List<string>();

        foreach (var entryId in entryIds)
        {
            statuses.Add((await TestData.GetWaitlistEntryAsync(entryId)).Status);
        }

        CollectionAssert.AreEqual(
            Enumerable.Repeat(WaitlistStatus.Promoted, 5).Concat(Enumerable.Repeat(WaitlistStatus.Waiting, 2)).ToList(),
            statuses);

        var enrollments = (await TestData.GetEnrollmentsAsync(scheduledClass.Id))
            .Where(x => x.Status == EnrollmentStatus.Active)
            .ToList();
        Assert.AreEqual(5, enrollments.Select(x => x.UserId).Distinct().Count());

        for (var i = 0; i < waiters.Count; i++)
        {
            Assert.AreEqual(i < 5 ? 0 : 1, (await TestData.GetPassAsync(waiters[i].Id)).RemainingVisits);
        }
    }

    [TestMethod]
    public async Task ParallelLeaveAndPromotion_EndInExactlyOneOutcome()
    {
        for (var round = 0; round < 5; round++)
        {
            var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
            var holder = await TestData.CreateUserAsync();
            var holderEnrollment = await holder.EnrollAsync(scheduledClass.Id, 1);
            var waiter = await TestData.CreateUserAsync(visits: 1);
            var entryId = await waiter.JoinWaitlistAsync(scheduledClass.Id, 1);

            var cancel = holder.Client.CancelAsync(holderEnrollment);
            var leave = waiter.Client.LeaveWaitlistAsync(entryId);
            await Task.WhenAll(cancel, leave);

            var entry = await TestData.GetWaitlistEntryAsync(entryId);
            var visits = (await TestData.GetPassAsync(waiter.Id)).RemainingVisits;

            if (entry.Status == WaitlistStatus.Promoted)
            {
                await ResponseAssert.ConflictAsync(leave.Result, "waitlist-fulfilled");
                Assert.AreEqual(0, visits);
                Assert.AreEqual(1, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
            }
            else
            {
                Assert.AreEqual(WaitlistStatus.Cancelled, entry.Status);
                Assert.AreEqual(HttpStatusCode.OK, leave.Result.StatusCode);
                Assert.AreEqual(1, visits);
                Assert.AreEqual(0, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
            }
        }
    }

    [TestMethod]
    public async Task ParallelWaitlistJoins_GetDistinctPositions()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(scheduledClass.Id, 1);
        var users = await CreateUsersAsync(10, visits: 1);

        var responses = await Task.WhenAll(users.Select(user => user.Client.JoinWaitlistAsync(scheduledClass.Id, 1)));

        var positions = new List<int>();

        foreach (var response in responses)
        {
            positions.Add((await ResponseAssert.StatusAsync(response, HttpStatusCode.Created)).GetProperty("position").GetInt32());
        }

        CollectionAssert.AreEquivalent(Enumerable.Range(1, 10).ToList(), positions);
    }

    [TestMethod]
    public async Task ParallelCancelOfSameEnrollment_RefundsOnce()
    {
        var user = await TestData.CreateUserAsync(visits: 3);
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 3);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => user.Client.CancelAsync(enrollmentId)));

        foreach (var response in responses)
        {
            var json = await ResponseAssert.StatusAsync(response, HttpStatusCode.OK);
            Assert.AreEqual(3, json.GetProperty("refundedVisits").GetInt32());
        }

        Assert.AreEqual(3, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    private static async Task<List<TestUser>> CreateUsersAsync(int count, int visits)
    {
        var users = new List<TestUser>();

        for (var i = 0; i < count; i++)
        {
            users.Add(await TestData.CreateUserAsync(visits: visits));
        }

        return users;
    }
}
