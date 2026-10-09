using System.Net;
using DataAccessLayer.Entities;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Endpoints;

[TestClass]
public class LeaveWaitlistTests : ApiTestBase
{
    [TestMethod]
    public async Task Leave_RemovesUserFromQueue()
    {
        var (user, entryId, _) = await JoinFullClassAsync();

        var json = await ResponseAssert.StatusAsync(await user.Client.LeaveWaitlistAsync(entryId), HttpStatusCode.OK);

        ResponseAssert.HasExactlyProperties(json, "id", "status");
        Assert.AreEqual(entryId, json.GetProperty("id").GetInt64());
        Assert.AreEqual("cancelled", json.GetProperty("status").GetString());
        Assert.AreEqual(WaitlistStatus.Cancelled, (await TestData.GetWaitlistEntryAsync(entryId)).Status);
    }

    [TestMethod]
    public async Task Leave_RepeatReturnsSameResult()
    {
        var (user, entryId, _) = await JoinFullClassAsync();

        var first = await ResponseAssert.StatusAsync(await user.Client.LeaveWaitlistAsync(entryId), HttpStatusCode.OK);
        var cancelledAt = (await TestData.GetWaitlistEntryAsync(entryId)).CancelledAt;
        Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await ResponseAssert.StatusAsync(await user.Client.LeaveWaitlistAsync(entryId), HttpStatusCode.OK);

        Assert.AreEqual(first.GetRawText(), second.GetRawText());
        Assert.AreEqual(cancelledAt, (await TestData.GetWaitlistEntryAsync(entryId)).CancelledAt);
    }

    [TestMethod]
    public async Task Leave_MovesOthersUp()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(scheduledClass.Id, 1);
        var first = await TestData.CreateUserAsync();
        var second = await TestData.CreateUserAsync();
        var firstEntry = await first.JoinWaitlistAsync(scheduledClass.Id, 1);
        await second.JoinWaitlistAsync(scheduledClass.Id, 1);

        await ResponseAssert.StatusAsync(await first.Client.LeaveWaitlistAsync(firstEntry), HttpStatusCode.OK);

        var newcomer = await TestData.CreateUserAsync();
        var json = await ResponseAssert.StatusAsync(
            await newcomer.Client.JoinWaitlistAsync(scheduledClass.Id, 1),
            HttpStatusCode.Created);
        Assert.AreEqual(2, json.GetProperty("position").GetInt32());
    }

    [TestMethod]
    public async Task Leave_UserDoesNotGetSeatFreedLater()
    {
        var holder = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        var holderEnrollment = await holder.EnrollAsync(scheduledClass.Id, 1);
        var waiter = await TestData.CreateUserAsync(visits: 5);
        var entryId = await waiter.JoinWaitlistAsync(scheduledClass.Id, 1);
        await ResponseAssert.StatusAsync(await waiter.Client.LeaveWaitlistAsync(entryId), HttpStatusCode.OK);

        await holder.CancelAsync(holderEnrollment);

        Assert.AreEqual(0, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
        Assert.AreEqual(5, (await TestData.GetPassAsync(waiter.Id)).RemainingVisits);
        Assert.AreEqual(WaitlistStatus.Cancelled, (await TestData.GetWaitlistEntryAsync(entryId)).Status);
    }

    [TestMethod]
    public async Task Leave_PromotedEntryIsWaitlistFulfilled()
    {
        var holder = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        var holderEnrollment = await holder.EnrollAsync(scheduledClass.Id, 1);
        var waiter = await TestData.CreateUserAsync();
        var entryId = await waiter.JoinWaitlistAsync(scheduledClass.Id, 1);
        await holder.CancelAsync(holderEnrollment);

        await ResponseAssert.ConflictAsync(await waiter.Client.LeaveWaitlistAsync(entryId), "waitlist-fulfilled");

        Assert.AreEqual(1, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    public async Task Leave_WorksAfterClassStartedOrCancelled()
    {
        var (user, entryId, classId) = await JoinFullClassAsync();
        await TestData.SetClassAsync(classId, cancelled: true);
        Clock.Advance(TimeSpan.FromDays(3));

        await ResponseAssert.StatusAsync(await user.Client.LeaveWaitlistAsync(entryId), HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task Leave_OtherUsersEntryIsNotFound()
    {
        var (_, entryId, _) = await JoinFullClassAsync();
        var stranger = await TestData.CreateUserAsync();

        await ResponseAssert.ProblemAsync(await stranger.Client.LeaveWaitlistAsync(entryId), HttpStatusCode.NotFound);
        Assert.AreEqual(WaitlistStatus.Waiting, (await TestData.GetWaitlistEntryAsync(entryId)).Status);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-2L)]
    [DataRow(long.MaxValue)]
    public async Task Leave_UnknownEntryIsNotFound(long id)
    {
        var user = await TestData.CreateUserAsync();

        await ResponseAssert.ProblemAsync(await user.Client.LeaveWaitlistAsync(id), HttpStatusCode.NotFound);
    }

    [TestMethod]
    [DataRow("abc")]
    [DataRow("-")]
    [DataRow("99999999999999999999")]
    public async Task Leave_NonNumericIdIsBadRequest(string id)
    {
        var user = await TestData.CreateUserAsync();

        await ResponseAssert.StatusAsync(
            await user.Client.PostAsync($"/enrollments/waitlist/{id}/cancel"),
            HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task Leave_RequiresAuthentication()
    {
        var response = await ApiClient.Anonymous().LeaveWaitlistAsync(1);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<(TestUser User, long EntryId, long ClassId)> JoinFullClassAsync()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(scheduledClass.Id, 1);

        return (user, await user.JoinWaitlistAsync(scheduledClass.Id, 1), scheduledClass.Id);
    }
}
