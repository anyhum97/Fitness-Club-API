using System.Net;
using DataAccessLayer.Entities;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Endpoints;

[TestClass]
public class CreateEnrollmentTests : ApiTestBase
{
    [TestMethod]
    public async Task Create_TakesSeatsAndChargesVisits()
    {
        var user = await TestData.CreateUserAsync(visits: 5);
        var scheduledClass = await TestData.CreateClassAsync(capacity: 10);

        var response = await user.Client.EnrollAsync(scheduledClass.Id, 3);

        var json = await ResponseAssert.StatusAsync(response, HttpStatusCode.Created);
        ResponseAssert.HasExactlyProperties(json, "id", "classId", "seats", "status", "createdAt");
        Assert.AreEqual(scheduledClass.Id, json.GetProperty("classId").GetInt64());
        Assert.AreEqual(3, json.GetProperty("seats").GetInt32());
        Assert.AreEqual("active", json.GetProperty("status").GetString());
        Assert.AreEqual(Now, json.GetProperty("createdAt").GetDateTime().ToUniversalTime());
        Assert.AreEqual(2, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
        Assert.AreEqual(3, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    public async Task Create_ReturnsLocationOfEnrollmentCard()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();

        var response = await user.Client.EnrollAsync(scheduledClass.Id, 1);
        var json = await ResponseAssert.StatusAsync(response, HttpStatusCode.Created);

        StringAssert.EndsWith(response.Headers.Location!.ToString(), $"/enrollments/{json.GetProperty("id").GetInt64()}");
    }

    [TestMethod]
    public async Task Create_SameUserCanHoldSeveralEnrollmentsOnOneClass()
    {
        var user = await TestData.CreateUserAsync(visits: 10);
        var scheduledClass = await TestData.CreateClassAsync(capacity: 5);

        await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1), HttpStatusCode.Created);
        await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 2), HttpStatusCode.Created);

        Assert.AreEqual(2, (await TestData.GetEnrollmentsAsync(scheduledClass.Id)).Count);
        Assert.AreEqual(7, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    [DataRow(10, 0, 10, DisplayName = "all seats of an empty class")]
    [DataRow(10, 9, 1, DisplayName = "the last free seat")]
    [DataRow(1, 0, 1, DisplayName = "class with capacity one")]
    public async Task Create_AllowsBookingUpToCapacity(int capacity, int alreadyBooked, int seats)
    {
        var user = await TestData.CreateUserAsync(visits: seats);
        var scheduledClass = await TestData.CreateClassAsync(capacity: capacity);

        if (alreadyBooked > 0)
        {
            await TestData.FillClassAsync(scheduledClass.Id, alreadyBooked);
        }

        await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, seats), HttpStatusCode.Created);

        Assert.AreEqual(capacity, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
        Assert.AreEqual(0, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    [DataRow(10, 0, 11, DisplayName = "capacity plus one on an empty class")]
    [DataRow(10, 10, 1, DisplayName = "full class")]
    [DataRow(10, 8, 3, DisplayName = "more than the remaining seats")]
    public async Task Create_RejectsMoreSeatsThanFree(int capacity, int alreadyBooked, int seats)
    {
        var user = await TestData.CreateUserAsync(visits: 50);
        var scheduledClass = await TestData.CreateClassAsync(capacity: capacity);

        if (alreadyBooked > 0)
        {
            await TestData.FillClassAsync(scheduledClass.Id, alreadyBooked);
        }

        await ResponseAssert.ConflictAsync(
            await user.Client.EnrollAsync(scheduledClass.Id, seats),
            "capacity-exceeded");

        Assert.AreEqual(alreadyBooked, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
        Assert.AreEqual(50, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Create_CancelledEnrollmentsDoNotOccupySeats()
    {
        var user = await TestData.CreateUserAsync();
        var other = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 2);

        await TestData.CreateEnrollmentAsync(other.Id, scheduledClass.Id, 2, EnrollmentStatus.Cancelled);

        await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 2), HttpStatusCode.Created);
    }

    [TestMethod]
    [DataRow(3, 3, true, DisplayName = "visits exactly enough")]
    [DataRow(2, 3, false, DisplayName = "one visit short")]
    [DataRow(0, 1, false, DisplayName = "exhausted pass")]
    public async Task Create_ChecksRemainingVisits(int visits, int seats, bool succeeds)
    {
        var user = await TestData.CreateUserAsync(visits: visits);
        var scheduledClass = await TestData.CreateClassAsync();

        var response = await user.Client.EnrollAsync(scheduledClass.Id, seats);

        if (succeeds)
        {
            await ResponseAssert.StatusAsync(response, HttpStatusCode.Created);
            Assert.AreEqual(0, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
        }
        else
        {
            await ResponseAssert.ConflictAsync(response, "pass-exhausted");
            Assert.AreEqual(visits, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
            Assert.AreEqual(0, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
        }
    }

    [TestMethod]
    [DataRow(0, true, DisplayName = "pass valid until today inclusive")]
    [DataRow(-1, false, DisplayName = "pass expired yesterday")]
    [DataRow(-365, false, DisplayName = "pass expired long ago")]
    public async Task Create_ChecksPassValidityAgainstRequestMoment(int validForDays, bool succeeds)
    {
        var user = await TestData.CreateUserAsync(validUntil: Clock.Today.AddDays(validForDays));
        var scheduledClass = await TestData.CreateClassAsync(startsIn: TimeSpan.FromDays(40));

        var response = await user.Client.EnrollAsync(scheduledClass.Id, 1);

        if (succeeds)
        {
            await ResponseAssert.StatusAsync(response, HttpStatusCode.Created);
        }
        else
        {
            await ResponseAssert.ConflictAsync(response, "pass-expired");
        }
    }

    [TestMethod]
    public async Task Create_PassValidUntilLastMomentOfDayStillWorks()
    {
        Clock.Advance(Clock.Today.AddDays(1).ToDateTime(TimeOnly.MinValue) - Now - TimeSpan.FromMicroseconds(1));
        var user = await TestData.CreateUserAsync(validUntil: Clock.Today);
        var scheduledClass = await TestData.CreateClassAsync();

        await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1), HttpStatusCode.Created);
    }

    [TestMethod]
    [DataRow(0, DisplayName = "class starting right now")]
    [DataRow(-1, DisplayName = "class started a second ago")]
    [DataRow(-7200, DisplayName = "class started two hours ago")]
    public async Task Create_RejectsStartedClass(int startsInSeconds)
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(startsIn: TimeSpan.FromSeconds(startsInSeconds));

        await ResponseAssert.ConflictAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1), "class-started");

        Assert.AreEqual(20, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Create_AllowsClassStartingInOneSecond()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(startsIn: TimeSpan.FromSeconds(1));

        await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1), HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task Create_RejectsCancelledClass()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(cancelled: true);

        await ResponseAssert.ConflictAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1), "class-cancelled");
    }

    [TestMethod]
    public async Task Create_ReportsClassCancelledBeforeClassStarted()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(startsIn: TimeSpan.FromHours(-1), cancelled: true);

        await ResponseAssert.ConflictAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1), "class-cancelled");
    }

    [TestMethod]
    public async Task Create_ReportsPassExpiredBeforePassExhausted()
    {
        var user = await TestData.CreateUserAsync(visits: 0, validUntil: Clock.Today.AddDays(-1));
        var scheduledClass = await TestData.CreateClassAsync();

        await ResponseAssert.ConflictAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1), "pass-expired");
    }

    [TestMethod]
    public async Task Create_ReportsPassProblemsBeforeCapacity()
    {
        var user = await TestData.CreateUserAsync(visits: 0);
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(scheduledClass.Id, 1);

        await ResponseAssert.ConflictAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1), "pass-exhausted");
    }

    [TestMethod]
    public async Task Create_DoesNotJoinWaitlistWhenFull()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(scheduledClass.Id, 1);

        await ResponseAssert.ConflictAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1), "capacity-exceeded");

        Assert.AreEqual(0, (await TestData.GetWaitlistAsync(scheduledClass.Id)).Count);
    }

    [TestMethod]
    public async Task Create_MayTakeFreeSeatEvenWhenSomeoneIsWaiting()
    {
        var waiter = await TestData.CreateUserAsync();
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 3);
        await TestData.FillClassAsync(scheduledClass.Id, 2);

        await ResponseAssert.StatusAsync(await waiter.Client.JoinWaitlistAsync(scheduledClass.Id, 2), HttpStatusCode.Created);
        await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1), HttpStatusCode.Created);
    }

    [TestMethod]
    [DataRow(0L, DisplayName = "zero id")]
    [DataRow(-5L, DisplayName = "negative id")]
    [DataRow(long.MaxValue, DisplayName = "long.MaxValue")]
    public async Task Create_ReturnsNotFoundForUnknownClass(long classId)
    {
        var user = await TestData.CreateUserAsync();

        await ResponseAssert.ProblemAsync(await user.Client.EnrollAsync(classId, 1), HttpStatusCode.NotFound);
    }

    [TestMethod]
    [DataRow("""{"classId":1,"seats":0}""", DisplayName = "seats zero")]
    [DataRow("""{"classId":1,"seats":-1}""", DisplayName = "seats negative")]
    [DataRow("""{"classId":1}""", DisplayName = "seats missing")]
    [DataRow("""{"seats":1}""", DisplayName = "classId missing")]
    [DataRow("""{"classId":null,"seats":1}""", DisplayName = "classId null")]
    [DataRow("""{"classId":1,"seats":null}""", DisplayName = "seats null")]
    [DataRow("""{"classId":"abc","seats":1}""", DisplayName = "classId not a number")]
    [DataRow("""{"classId":1,"seats":"two"}""", DisplayName = "seats not a number")]
    [DataRow("""{"classId":1,"seats":1.5}""", DisplayName = "seats fractional")]
    [DataRow("""{"classId":1,"seats":true}""", DisplayName = "seats boolean")]
    [DataRow("""{"classId":9223372036854775808,"seats":1}""", DisplayName = "classId beyond long")]
    [DataRow("""{"classId":1,"seats":2147483648}""", DisplayName = "seats beyond int")]
    [DataRow("""{"classId":1,"seats":1""", DisplayName = "broken JSON")]
    [DataRow("""[1,2]""", DisplayName = "array instead of object")]
    [DataRow("null", DisplayName = "JSON null")]
    [DataRow("", DisplayName = "empty body")]
    [DataRow("   ", DisplayName = "whitespace body")]
    public async Task Create_RejectsInvalidBody(string body)
    {
        var user = await TestData.CreateUserAsync();

        var response = await user.Client.PostRawAsync("/enrollments", body, ApiClient.NewKey());

        await ResponseAssert.StatusAsync(response, HttpStatusCode.BadRequest);
        Assert.AreEqual(20, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Create_RejectsMissingBodyWithoutContent()
    {
        var user = await TestData.CreateUserAsync();

        var response = await user.Client.PostRawAsync("/enrollments", null, ApiClient.NewKey());

        Assert.IsTrue(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType,
            response.StatusCode.ToString());
    }

    [TestMethod]
    public async Task Create_IgnoresUnknownFields()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();

        var response = await user.Client.PostRawAsync(
            "/enrollments",
            $$"""{"classId":{{scheduledClass.Id}},"seats":1,"userId":999,"status":"cancelled","extra":[1, 2] }""",
            ApiClient.NewKey());

        var json = await ResponseAssert.StatusAsync(response, HttpStatusCode.Created);
        Assert.AreEqual("active", json.GetProperty("status").GetString());
    }

    [TestMethod]
    [DataRow(null, DisplayName = "header missing")]
    [DataRow("", DisplayName = "header empty")]
    [DataRow("   ", DisplayName = "header whitespace")]
    public async Task Create_RequiresIdempotencyKey(string? key)
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();

        var response = await user.Client.PostRawAsync(
            "/enrollments",
            $$"""{"classId":{{scheduledClass.Id}},"seats":1}""",
            key);

        await ResponseAssert.ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.AreEqual(0, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    public async Task Create_AcceptsVeryLongIdempotencyKey()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var key = new string('k', 10_000);

        await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1, key), HttpStatusCode.Created);
        await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1, key), HttpStatusCode.Created);

        Assert.AreEqual(1, (await TestData.GetEnrollmentsAsync(scheduledClass.Id)).Count);
    }

    [TestMethod]
    public async Task Create_RequiresAuthentication()
    {
        var scheduledClass = await TestData.CreateClassAsync();

        var response = await ApiClient.Anonymous().EnrollAsync(scheduledClass.Id, 1);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
