using System.Net;
using DataAccessLayer.Entities;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Scenarios;

[TestClass]
public class WaitlistPromotionTests : ApiTestBase
{
    [TestMethod]
    public async Task Cancel_PromotesFirstWaiterAndChargesVisitsAtPromotion()
    {
        var (scheduledClass, holder, holderEnrollment) = await FullClassAsync(capacity: 2, holderSeats: 2);
        var waiter = await TestData.CreateUserAsync(visits: 5);
        var entryId = await waiter.JoinWaitlistAsync(scheduledClass.Id, 2);
        Assert.AreEqual(5, (await TestData.GetPassAsync(waiter.Id)).RemainingVisits);

        await holder.CancelAsync(holderEnrollment);

        var entry = await TestData.GetWaitlistEntryAsync(entryId);
        Assert.AreEqual(WaitlistStatus.Promoted, entry.Status);
        Assert.IsNotNull(entry.EnrollmentId);
        var enrollment = await TestData.GetEnrollmentAsync(entry.EnrollmentId.Value);
        Assert.AreEqual(EnrollmentStatus.Active, enrollment.Status);
        Assert.AreEqual(2, enrollment.Seats);
        Assert.AreEqual(waiter.Id, enrollment.UserId);
        Assert.AreEqual(3, (await TestData.GetPassAsync(waiter.Id)).RemainingVisits);
        Assert.AreEqual(2, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    public async Task Promotion_CreatesEnrollmentVisibleToWaiter()
    {
        var (scheduledClass, holder, holderEnrollment) = await FullClassAsync(capacity: 1, holderSeats: 1);
        var waiter = await TestData.CreateUserAsync();
        var entryId = await waiter.JoinWaitlistAsync(scheduledClass.Id, 1);

        await holder.CancelAsync(holderEnrollment);

        var enrollmentId = (await TestData.GetWaitlistEntryAsync(entryId)).EnrollmentId!.Value;
        var card = await ResponseAssert.StatusAsync(await waiter.Client.GetCardAsync(enrollmentId), HttpStatusCode.OK);
        Assert.AreEqual("active", card.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task Promotion_FollowsQueueOrder()
    {
        var (scheduledClass, holder, holderEnrollment) = await FullClassAsync(capacity: 1, holderSeats: 1);
        var first = await TestData.CreateUserAsync();
        var second = await TestData.CreateUserAsync();
        var firstEntry = await first.JoinWaitlistAsync(scheduledClass.Id, 1);
        Clock.Advance(TimeSpan.FromSeconds(1));
        var secondEntry = await second.JoinWaitlistAsync(scheduledClass.Id, 1);

        await holder.CancelAsync(holderEnrollment);

        Assert.AreEqual(WaitlistStatus.Promoted, (await TestData.GetWaitlistEntryAsync(firstEntry)).Status);
        Assert.AreEqual(WaitlistStatus.Waiting, (await TestData.GetWaitlistEntryAsync(secondEntry)).Status);
    }

    [TestMethod]
    public async Task Promotion_SkipsWaiterWhoNeedsMoreSeatsAndKeepsTheirPosition()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 3);
        var holder = await TestData.CreateUserAsync();
        var holderEnrollment = await holder.EnrollAsync(scheduledClass.Id, 1);
        await TestData.FillClassAsync(scheduledClass.Id, 2);
        var big = await TestData.CreateUserAsync();
        var small = await TestData.CreateUserAsync(visits: 4);
        var bigEntry = await big.JoinWaitlistAsync(scheduledClass.Id, 2);
        Clock.Advance(TimeSpan.FromSeconds(1));
        var smallEntry = await small.JoinWaitlistAsync(scheduledClass.Id, 1);

        await holder.CancelAsync(holderEnrollment);

        Assert.AreEqual(WaitlistStatus.Waiting, (await TestData.GetWaitlistEntryAsync(bigEntry)).Status);
        Assert.AreEqual(WaitlistStatus.Promoted, (await TestData.GetWaitlistEntryAsync(smallEntry)).Status);
        Assert.AreEqual(3, (await TestData.GetPassAsync(small.Id)).RemainingVisits);

        var newcomer = await TestData.CreateUserAsync();
        var json = await ResponseAssert.StatusAsync(
            await newcomer.Client.JoinWaitlistAsync(scheduledClass.Id, 1),
            HttpStatusCode.Created);
        Assert.AreEqual(2, json.GetProperty("position").GetInt32());
    }

    [TestMethod]
    public async Task Promotion_WaiterWhoNeedsMoreSeatsGetsThemWhenEnoughAreFree()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 3);
        var first = await TestData.CreateUserAsync();
        var second = await TestData.CreateUserAsync();
        var firstEnrollment = await first.EnrollAsync(scheduledClass.Id, 1);
        var secondEnrollment = await second.EnrollAsync(scheduledClass.Id, 2);
        var big = await TestData.CreateUserAsync();
        var bigEntry = await big.JoinWaitlistAsync(scheduledClass.Id, 3);

        await first.CancelAsync(firstEnrollment);
        Assert.AreEqual(WaitlistStatus.Waiting, (await TestData.GetWaitlistEntryAsync(bigEntry)).Status);
        Assert.AreEqual(2, await TestData.GetActiveSeatsAsync(scheduledClass.Id));

        await second.CancelAsync(secondEnrollment);
        Assert.AreEqual(WaitlistStatus.Promoted, (await TestData.GetWaitlistEntryAsync(bigEntry)).Status);
        Assert.AreEqual(3, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    public async Task Promotion_FillsSeveralWaitersFromOneCancellation()
    {
        var (scheduledClass, holder, holderEnrollment) = await FullClassAsync(capacity: 4, holderSeats: 4);
        var waiters = new List<(TestUser User, long EntryId)>();

        foreach (var seats in new[] { 1, 2, 2, 1 })
        {
            var waiter = await TestData.CreateUserAsync();
            waiters.Add((waiter, await waiter.JoinWaitlistAsync(scheduledClass.Id, seats)));
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        await holder.CancelAsync(holderEnrollment);

        var statuses = new List<string>();

        foreach (var (_, entryId) in waiters)
        {
            statuses.Add((await TestData.GetWaitlistEntryAsync(entryId)).Status);
        }

        CollectionAssert.AreEqual(
            new[] { WaitlistStatus.Promoted, WaitlistStatus.Promoted, WaitlistStatus.Waiting, WaitlistStatus.Promoted },
            statuses);
        Assert.AreEqual(4, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    [DataRow(0, 30, DisplayName = "exhausted pass")]
    [DataRow(1, 30, DisplayName = "one visit short")]
    [DataRow(10, -1, DisplayName = "expired pass")]
    public async Task Promotion_SkipsWaiterWithUnsuitablePassWhoStaysInQueue(int visits, int validForDays)
    {
        var (scheduledClass, holder, holderEnrollment) = await FullClassAsync(capacity: 2, holderSeats: 2);
        var unsuitable = await TestData.CreateUserAsync(visits: visits, validUntil: Clock.Today.AddDays(validForDays));
        var suitable = await TestData.CreateUserAsync(visits: 2);
        var unsuitableEntry = await unsuitable.JoinWaitlistAsync(scheduledClass.Id, 2);
        Clock.Advance(TimeSpan.FromSeconds(1));
        var suitableEntry = await suitable.JoinWaitlistAsync(scheduledClass.Id, 2);

        await holder.CancelAsync(holderEnrollment);

        Assert.AreEqual(WaitlistStatus.Waiting, (await TestData.GetWaitlistEntryAsync(unsuitableEntry)).Status);
        Assert.AreEqual(visits, (await TestData.GetPassAsync(unsuitable.Id)).RemainingVisits);
        Assert.AreEqual(WaitlistStatus.Promoted, (await TestData.GetWaitlistEntryAsync(suitableEntry)).Status);
        Assert.AreEqual(0, (await TestData.GetPassAsync(suitable.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Promotion_PassExpiringTodayIsStillValid()
    {
        var (scheduledClass, holder, holderEnrollment) = await FullClassAsync(capacity: 1, holderSeats: 1);
        var waiter = await TestData.CreateUserAsync(validUntil: Clock.Today);
        var entryId = await waiter.JoinWaitlistAsync(scheduledClass.Id, 1);

        await holder.CancelAsync(holderEnrollment);

        Assert.AreEqual(WaitlistStatus.Promoted, (await TestData.GetWaitlistEntryAsync(entryId)).Status);
    }

    [TestMethod]
    public async Task Promotion_NobodySuitableLeavesSeatsFree()
    {
        var (scheduledClass, holder, holderEnrollment) = await FullClassAsync(capacity: 1, holderSeats: 1);
        var waiter = await TestData.CreateUserAsync(visits: 0);
        var entryId = await waiter.JoinWaitlistAsync(scheduledClass.Id, 1);

        await holder.CancelAsync(holderEnrollment);

        Assert.AreEqual(WaitlistStatus.Waiting, (await TestData.GetWaitlistEntryAsync(entryId)).Status);
        Assert.AreEqual(0, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
        await ResponseAssert.StatusAsync(
            await (await TestData.CreateUserAsync()).Client.EnrollAsync(scheduledClass.Id, 1),
            HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task Promotion_HappensOnLateCancellationToo()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1, startsIn: TimeSpan.FromMinutes(30));
        var holder = await TestData.CreateUserAsync();
        var holderEnrollment = await holder.EnrollAsync(scheduledClass.Id, 1);
        var waiter = await TestData.CreateUserAsync();
        var entryId = await waiter.JoinWaitlistAsync(scheduledClass.Id, 1);

        Assert.AreEqual(0, await holder.CancelAsync(holderEnrollment));

        Assert.AreEqual(WaitlistStatus.Promoted, (await TestData.GetWaitlistEntryAsync(entryId)).Status);
    }

    [TestMethod]
    public async Task Promotion_DoesNotHappenOnClassCancelledByClub()
    {
        var (scheduledClass, holder, holderEnrollment) = await FullClassAsync(capacity: 1, holderSeats: 1);
        var waiter = await TestData.CreateUserAsync(visits: 3);
        var entryId = await waiter.JoinWaitlistAsync(scheduledClass.Id, 1);
        await TestData.SetClassAsync(scheduledClass.Id, cancelled: true);

        await holder.CancelAsync(holderEnrollment);

        Assert.AreEqual(WaitlistStatus.Waiting, (await TestData.GetWaitlistEntryAsync(entryId)).Status);
        Assert.AreEqual(3, (await TestData.GetPassAsync(waiter.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Decrease_PromotesQueue()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 4);
        var holder = await TestData.CreateUserAsync(visits: 4);
        var holderEnrollment = await holder.EnrollAsync(scheduledClass.Id, 4);
        var waiter = await TestData.CreateUserAsync(visits: 2);
        var entryId = await waiter.JoinWaitlistAsync(scheduledClass.Id, 2);

        await ResponseAssert.StatusAsync(await holder.Client.ChangeSeatsAsync(holderEnrollment, 2), HttpStatusCode.OK);

        Assert.AreEqual(WaitlistStatus.Promoted, (await TestData.GetWaitlistEntryAsync(entryId)).Status);
        Assert.AreEqual(0, (await TestData.GetPassAsync(waiter.Id)).RemainingVisits);
        Assert.AreEqual(2, (await TestData.GetPassAsync(holder.Id)).RemainingVisits);
        Assert.AreEqual(4, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    public async Task Increase_DoesNotTakeSeatsAwayFromQueueByItself()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 3);
        var holder = await TestData.CreateUserAsync();
        var holderEnrollment = await holder.EnrollAsync(scheduledClass.Id, 1);
        await TestData.FillClassAsync(scheduledClass.Id, 2);
        var waiter = await TestData.CreateUserAsync();
        await waiter.JoinWaitlistAsync(scheduledClass.Id, 1);

        await ResponseAssert.ConflictAsync(await holder.Client.ChangeSeatsAsync(holderEnrollment, 2), "capacity-exceeded");
    }

    [TestMethod]
    public async Task Promotion_WaiterCanBeTheCancellingUserThemselves()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 2);
        var user = await TestData.CreateUserAsync(visits: 10);
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 2);
        var entryId = await user.JoinWaitlistAsync(scheduledClass.Id, 1);

        Assert.AreEqual(2, await user.CancelAsync(enrollmentId));

        Assert.AreEqual(WaitlistStatus.Promoted, (await TestData.GetWaitlistEntryAsync(entryId)).Status);
        Assert.AreEqual(9, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Promotion_SameUserCanBeWaitingAgainAfterPromotion()
    {
        var (scheduledClass, holder, holderEnrollment) = await FullClassAsync(capacity: 1, holderSeats: 1);
        var waiter = await TestData.CreateUserAsync();
        await waiter.JoinWaitlistAsync(scheduledClass.Id, 1);
        await holder.CancelAsync(holderEnrollment);

        await ResponseAssert.StatusAsync(await waiter.Client.JoinWaitlistAsync(scheduledClass.Id, 1), HttpStatusCode.Created);
    }

    private static async Task<(Class Class, TestUser Holder, long HolderEnrollment)> FullClassAsync(
        int capacity,
        int holderSeats)
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: capacity);
        var holder = await TestData.CreateUserAsync(visits: 20);
        var holderEnrollment = await holder.EnrollAsync(scheduledClass.Id, holderSeats);

        if (capacity > holderSeats)
        {
            await TestData.FillClassAsync(scheduledClass.Id, capacity - holderSeats);
        }

        return (scheduledClass, holder, holderEnrollment);
    }
}
