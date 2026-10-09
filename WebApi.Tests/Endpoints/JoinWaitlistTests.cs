using System.Net;
using DataAccessLayer.Entities;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Endpoints;

[TestClass]
public class JoinWaitlistTests : ApiTestBase
{
    [TestMethod]
    public async Task Join_PutsUserIntoQueueWithoutChargingVisits()
    {
        var user = await TestData.CreateUserAsync(visits: 5);
        var scheduledClass = await TestData.CreateClassAsync(capacity: 3);
        await TestData.FillClassAsync(scheduledClass.Id, 3);

        var json = await ResponseAssert.StatusAsync(
            await user.Client.JoinWaitlistAsync(scheduledClass.Id, 2),
            HttpStatusCode.Created);

        ResponseAssert.HasExactlyProperties(json, "id", "classId", "seats", "position", "createdAt");
        Assert.AreEqual(scheduledClass.Id, json.GetProperty("classId").GetInt64());
        Assert.AreEqual(2, json.GetProperty("seats").GetInt32());
        Assert.AreEqual(1, json.GetProperty("position").GetInt32());
        Assert.AreEqual(Now, json.GetProperty("createdAt").GetDateTime());
        Assert.AreEqual(5, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
        Assert.AreEqual(3, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    public async Task Join_PositionsFollowJoinOrder()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 2);
        await TestData.FillClassAsync(scheduledClass.Id, 2);

        for (var expected = 1; expected <= 4; expected++)
        {
            var user = await TestData.CreateUserAsync();
            var json = await ResponseAssert.StatusAsync(
                await user.Client.JoinWaitlistAsync(scheduledClass.Id, 1),
                HttpStatusCode.Created);

            Assert.AreEqual(expected, json.GetProperty("position").GetInt32());
            Clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [TestMethod]
    public async Task Join_PositionCountsOnlyThoseStillWaiting()
    {
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(scheduledClass.Id, 1);
        var first = await TestData.CreateUserAsync();
        var second = await TestData.CreateUserAsync();
        var third = await TestData.CreateUserAsync();
        var firstEntry = await first.JoinWaitlistAsync(scheduledClass.Id, 1);
        await second.JoinWaitlistAsync(scheduledClass.Id, 1);
        await ResponseAssert.StatusAsync(await first.Client.LeaveWaitlistAsync(firstEntry), HttpStatusCode.OK);

        var json = await ResponseAssert.StatusAsync(
            await third.Client.JoinWaitlistAsync(scheduledClass.Id, 1),
            HttpStatusCode.Created);

        Assert.AreEqual(2, json.GetProperty("position").GetInt32());
    }

    [TestMethod]
    [DataRow(5, 3, 2, DisplayName = "free seats exactly as requested")]
    [DataRow(5, 0, 1, DisplayName = "empty class")]
    [DataRow(5, 2, 1, DisplayName = "more free seats than requested")]
    public async Task Join_RejectsWhenSeatsAreAvailable(int capacity, int booked, int seats)
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: capacity);

        if (booked > 0)
        {
            await TestData.FillClassAsync(scheduledClass.Id, booked);
        }

        await ResponseAssert.ConflictAsync(await user.Client.JoinWaitlistAsync(scheduledClass.Id, seats), "seats-available");
        Assert.AreEqual(0, (await TestData.GetWaitlistAsync(scheduledClass.Id)).Count);
    }

    [TestMethod]
    public async Task Join_AllowedWhenFreeSeatsAreOneShort()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 5);
        await TestData.FillClassAsync(scheduledClass.Id, 3);

        await ResponseAssert.StatusAsync(await user.Client.JoinWaitlistAsync(scheduledClass.Id, 3), HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task Join_RejectsSecondEntryForSameClass()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(scheduledClass.Id, 1);
        await user.JoinWaitlistAsync(scheduledClass.Id, 1);

        await ResponseAssert.ConflictAsync(await user.Client.JoinWaitlistAsync(scheduledClass.Id, 1), "already-waiting");
        await ResponseAssert.ConflictAsync(await user.Client.JoinWaitlistAsync(scheduledClass.Id, 1), "already-waiting");
        Assert.AreEqual(1, (await TestData.GetWaitlistAsync(scheduledClass.Id)).Count);
    }

    [TestMethod]
    public async Task Join_AllowedAgainAfterLeaving()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(scheduledClass.Id, 1);
        var entryId = await user.JoinWaitlistAsync(scheduledClass.Id, 1);
        await ResponseAssert.StatusAsync(await user.Client.LeaveWaitlistAsync(entryId), HttpStatusCode.OK);

        await ResponseAssert.StatusAsync(await user.Client.JoinWaitlistAsync(scheduledClass.Id, 1), HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task Join_UserWithActiveEnrollmentCanWaitForMoreSeats()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 2);
        await user.EnrollAsync(scheduledClass.Id, 2);

        await ResponseAssert.StatusAsync(await user.Client.JoinWaitlistAsync(scheduledClass.Id, 1), HttpStatusCode.Created);
    }

    [TestMethod]
    [DataRow(4, 0, 5, DisplayName = "capacity plus one on an empty class")]
    [DataRow(4, 4, 5, DisplayName = "capacity plus one on a full class")]
    [DataRow(1, 1, 100, DisplayName = "far beyond capacity")]
    public async Task Join_AcceptsMoreSeatsThanCapacityBecauseFreeSeatsAreFewer(int capacity, int booked, int seats)
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: capacity);

        if (booked > 0)
        {
            await TestData.FillClassAsync(scheduledClass.Id, booked);
        }

        var json = await ResponseAssert.StatusAsync(
            await user.Client.JoinWaitlistAsync(scheduledClass.Id, seats),
            HttpStatusCode.Created);

        Assert.AreEqual(seats, json.GetProperty("seats").GetInt32());
        Assert.AreEqual(1, json.GetProperty("position").GetInt32());
    }

    [TestMethod]
    public async Task Join_AllowsSeatsEqualToCapacity()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 4);
        await TestData.FillClassAsync(scheduledClass.Id, 1);

        await ResponseAssert.StatusAsync(await user.Client.JoinWaitlistAsync(scheduledClass.Id, 4), HttpStatusCode.Created);
    }

    [TestMethod]
    [DataRow(0, DisplayName = "class starting right now")]
    [DataRow(-60, DisplayName = "class started a minute ago")]
    public async Task Join_RejectsStartedClass(int startsInSeconds)
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1, startsIn: TimeSpan.FromSeconds(startsInSeconds));
        await TestData.FillClassAsync(scheduledClass.Id, 1);

        await ResponseAssert.ConflictAsync(await user.Client.JoinWaitlistAsync(scheduledClass.Id, 1), "class-started");
    }

    [TestMethod]
    public async Task Join_RejectsCancelledClass()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1, cancelled: true);

        await ResponseAssert.ConflictAsync(await user.Client.JoinWaitlistAsync(scheduledClass.Id, 1), "class-cancelled");
    }

    [TestMethod]
    [DataRow(0, -1, DisplayName = "expired pass")]
    [DataRow(0, 30, DisplayName = "exhausted pass")]
    public async Task Join_DoesNotCheckPass(int visits, int validForDays)
    {
        var user = await TestData.CreateUserAsync(visits: visits, validUntil: Clock.Today.AddDays(validForDays));
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(scheduledClass.Id, 1);

        await ResponseAssert.StatusAsync(await user.Client.JoinWaitlistAsync(scheduledClass.Id, 1), HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task Join_UnknownClassIsNotFound()
    {
        var user = await TestData.CreateUserAsync();

        await ResponseAssert.ProblemAsync(await user.Client.JoinWaitlistAsync(long.MaxValue, 1), HttpStatusCode.NotFound);
    }

    [TestMethod]
    [DataRow("""{"classId":1,"seats":0}""", DisplayName = "seats zero")]
    [DataRow("""{"classId":1}""", DisplayName = "seats missing")]
    [DataRow("""{"seats":1}""", DisplayName = "classId missing")]
    [DataRow("""{"classId":"x","seats":1}""", DisplayName = "classId not a number")]
    [DataRow("""{"classId":1,"seats":1.25}""", DisplayName = "seats fractional")]
    [DataRow("""{"classId":"1","seats":1}""", DisplayName = "classId as numeric string")]
    [DataRow("""{"classId":1,"seats":"1"}""", DisplayName = "seats as numeric string")]
    [DataRow("""{"classId":1,""", DisplayName = "broken JSON")]
    [DataRow("", DisplayName = "empty body")]
    public async Task Join_RejectsInvalidBody(string body)
    {
        var user = await TestData.CreateUserAsync();

        var response = await user.Client.PostRawAsync("/enrollments/waitlist", body, ApiClient.NewKey());

        await ResponseAssert.StatusAsync(response, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t ")]
    public async Task Join_RequiresIdempotencyKey(string? key)
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(scheduledClass.Id, 1);

        var response = await user.Client.PostRawAsync(
            "/enrollments/waitlist",
            $$"""{"classId":{{scheduledClass.Id}},"seats":1}""",
            key);

        await ResponseAssert.ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.AreEqual(0, (await TestData.GetWaitlistAsync(scheduledClass.Id)).Count);
    }

    [TestMethod]
    public async Task Join_RepeatWithSameKeyReturnsFirstResponse()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(scheduledClass.Id, 1);
        var key = ApiClient.NewKey();

        var first = await ResponseAssert.StatusAsync(
            await user.Client.JoinWaitlistAsync(scheduledClass.Id, 1, key),
            HttpStatusCode.Created);
        Clock.Advance(TimeSpan.FromMinutes(5));
        var second = await ResponseAssert.StatusAsync(
            await user.Client.JoinWaitlistAsync(scheduledClass.Id, 1, key),
            HttpStatusCode.Created);

        Assert.AreEqual(first.GetRawText(), second.GetRawText());
        Assert.AreEqual(1, (await TestData.GetWaitlistAsync(scheduledClass.Id)).Count);
    }

    [TestMethod]
    public async Task Join_SameKeyWithDifferentBodyIsConflict()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 2);
        await TestData.FillClassAsync(scheduledClass.Id, 2);
        var key = ApiClient.NewKey();
        await ResponseAssert.StatusAsync(await user.Client.JoinWaitlistAsync(scheduledClass.Id, 1, key), HttpStatusCode.Created);

        await ResponseAssert.ConflictAsync(
            await user.Client.JoinWaitlistAsync(scheduledClass.Id, 2, key),
            "idempotency-key-conflict");
    }

    [TestMethod]
    public async Task Join_KeyIsSeparateFromEnrollmentKeys()
    {
        var user = await TestData.CreateUserAsync();
        var open = await TestData.CreateClassAsync(capacity: 5);
        var full = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(full.Id, 1);
        var key = ApiClient.NewKey();

        await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(open.Id, 1, key), HttpStatusCode.Created);
        await ResponseAssert.StatusAsync(await user.Client.JoinWaitlistAsync(full.Id, 1, key), HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task Join_RequiresAuthentication()
    {
        var response = await ApiClient.Anonymous().JoinWaitlistAsync(1, 1);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Join_StoresWaitingEntry()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(scheduledClass.Id, 1);

        var entryId = await user.JoinWaitlistAsync(scheduledClass.Id, 1);

        var entry = await TestData.GetWaitlistEntryAsync(entryId);
        Assert.AreEqual(WaitlistStatus.Waiting, entry.Status);
        Assert.AreEqual(user.Id, entry.UserId);
    }
}
