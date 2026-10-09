using System.Net;
using DataAccessLayer.Entities;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Endpoints;

[TestClass]
public class ChangeSeatsTests : ApiTestBase
{
    [TestMethod]
    public async Task Increase_ChargesDifferenceAndReturnsFullCard()
    {
        var user = await TestData.CreateUserAsync(visits: 10);
        var scheduledClass = await TestData.CreateClassAsync(capacity: 10);
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 2);

        var response = await user.Client.ChangeSeatsAsync(enrollmentId, 5);

        var json = await ResponseAssert.StatusAsync(response, HttpStatusCode.OK);
        ResponseAssert.HasExactlyProperties(json, "id", "status", "seats", "createdAt", "class", "pass");
        ResponseAssert.HasExactlyProperties(
            json.GetProperty("class"),
            "id", "title", "startsAt", "durationMinutes", "isCancelled", "room");
        ResponseAssert.HasExactlyProperties(json.GetProperty("class").GetProperty("room"), "id", "name");
        ResponseAssert.HasExactlyProperties(json.GetProperty("pass"), "remainingVisits", "validUntil");
        Assert.AreEqual(5, json.GetProperty("seats").GetInt32());
        Assert.AreEqual(5, json.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual(5, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
        Assert.AreEqual(5, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    [DataRow(10, 0, 8, 10, DisplayName = "increase to exactly capacity")]
    [DataRow(10, 8, 1, 2, DisplayName = "increase into the last seat")]
    public async Task Increase_AllowsUpToCapacity(int capacity, int otherSeats, int ownSeats, int newSeats)
    {
        var user = await TestData.CreateUserAsync(visits: 20);
        var scheduledClass = await TestData.CreateClassAsync(capacity: capacity);
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, ownSeats);

        if (otherSeats > 0)
        {
            await TestData.FillClassAsync(scheduledClass.Id, otherSeats);
        }

        await ResponseAssert.StatusAsync(await user.Client.ChangeSeatsAsync(enrollmentId, newSeats), HttpStatusCode.OK);

        Assert.AreEqual(capacity, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    public async Task Increase_RejectsWhenOneSeatShort()
    {
        var user = await TestData.CreateUserAsync(visits: 20);
        var scheduledClass = await TestData.CreateClassAsync(capacity: 10);
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 2);
        await TestData.FillClassAsync(scheduledClass.Id, 6);

        await ResponseAssert.ConflictAsync(await user.Client.ChangeSeatsAsync(enrollmentId, 5), "capacity-exceeded");

        Assert.AreEqual(2, (await TestData.GetEnrollmentAsync(enrollmentId)).Seats);
        Assert.AreEqual(18, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    [DataRow(3, 5, true, DisplayName = "visits exactly enough for the difference")]
    [DataRow(2, 5, false, DisplayName = "one visit short for the difference")]
    public async Task Increase_ChecksVisitsForDifferenceOnly(int remainingAfterBooking, int newSeats, bool succeeds)
    {
        var user = await TestData.CreateUserAsync(visits: 2 + remainingAfterBooking);
        var scheduledClass = await TestData.CreateClassAsync(capacity: 10);
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 2);

        var response = await user.Client.ChangeSeatsAsync(enrollmentId, newSeats);

        if (succeeds)
        {
            await ResponseAssert.StatusAsync(response, HttpStatusCode.OK);
            Assert.AreEqual(0, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
        }
        else
        {
            await ResponseAssert.ConflictAsync(response, "pass-exhausted");
            Assert.AreEqual(remainingAfterBooking, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
        }
    }

    [TestMethod]
    public async Task Increase_RejectsExpiredPass()
    {
        var user = await TestData.CreateUserAsync(visits: 10);
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 1);
        await TestData.SetPassAsync(user.Id, validUntil: Clock.Today.AddDays(-1));

        await ResponseAssert.ConflictAsync(await user.Client.ChangeSeatsAsync(enrollmentId, 2), "pass-expired");
    }

    [TestMethod]
    public async Task Decrease_RefundsDifferenceAndFreesSeats()
    {
        var user = await TestData.CreateUserAsync(visits: 10);
        var scheduledClass = await TestData.CreateClassAsync(capacity: 10);
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 4);

        var card = await ResponseAssert.StatusAsync(await user.Client.ChangeSeatsAsync(enrollmentId, 1), HttpStatusCode.OK);

        Assert.AreEqual(1, card.GetProperty("seats").GetInt32());
        Assert.AreEqual(9, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual(1, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    [DataRow(true, false, DisplayName = "expired pass")]
    [DataRow(false, true, DisplayName = "exhausted pass")]
    [DataRow(true, true, DisplayName = "expired and exhausted pass")]
    public async Task Decrease_RefundsEvenWithExpiredOrExhaustedPass(bool expired, bool exhausted)
    {
        var user = await TestData.CreateUserAsync(visits: 3);
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 3);
        await TestData.SetPassAsync(
            user.Id,
            visits: exhausted ? 0 : 5,
            validUntil: expired ? Clock.Today.AddDays(-10) : null);

        await ResponseAssert.StatusAsync(await user.Client.ChangeSeatsAsync(enrollmentId, 1), HttpStatusCode.OK);

        Assert.AreEqual((exhausted ? 0 : 5) + 2, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Decrease_RefundsEvenMinutesBeforeStart()
    {
        var user = await TestData.CreateUserAsync(visits: 4);
        var scheduledClass = await TestData.CreateClassAsync(startsIn: TimeSpan.FromMinutes(10));
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 4);

        await ResponseAssert.StatusAsync(await user.Client.ChangeSeatsAsync(enrollmentId, 1), HttpStatusCode.OK);

        Assert.AreEqual(3, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task SameSeats_ReturnsCardWithoutChanges()
    {
        var user = await TestData.CreateUserAsync(visits: 5);
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 2);

        var card = await ResponseAssert.StatusAsync(await user.Client.ChangeSeatsAsync(enrollmentId, 2), HttpStatusCode.OK);

        Assert.AreEqual(2, card.GetProperty("seats").GetInt32());
        Assert.AreEqual(3, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task SameSeats_StillPassesStateChecks()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(startsIn: TimeSpan.FromHours(3));
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 2);
        Clock.Advance(TimeSpan.FromHours(3));

        await ResponseAssert.ConflictAsync(await user.Client.ChangeSeatsAsync(enrollmentId, 2), "class-started");
    }

    [TestMethod]
    public async Task SameSeats_WithExpiredPassIsAllowed()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 2);
        await TestData.SetPassAsync(user.Id, visits: 0, validUntil: Clock.Today.AddDays(-1));

        await ResponseAssert.StatusAsync(await user.Client.ChangeSeatsAsync(enrollmentId, 2), HttpStatusCode.OK);
    }

    [TestMethod]
    [DataRow(2, DisplayName = "same seats")]
    [DataRow(1, DisplayName = "decrease")]
    [DataRow(3, DisplayName = "increase")]
    public async Task CancelledEnrollment_CannotBeChanged(int seats)
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 2);
        await ResponseAssert.StatusAsync(await user.Client.CancelAsync(enrollmentId), HttpStatusCode.OK);

        await ResponseAssert.ConflictAsync(await user.Client.ChangeSeatsAsync(enrollmentId, seats), "enrollment-cancelled");
    }

    [TestMethod]
    public async Task CancelledEnrollment_IsReportedBeforeCancelledClass()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollment = await TestData.CreateEnrollmentAsync(user.Id, scheduledClass.Id, 2, EnrollmentStatus.Cancelled);
        await TestData.SetClassAsync(scheduledClass.Id, cancelled: true);

        await ResponseAssert.ConflictAsync(await user.Client.ChangeSeatsAsync(enrollment.Id, 1), "enrollment-cancelled");
    }

    [TestMethod]
    [DataRow(1, DisplayName = "decrease")]
    [DataRow(2, DisplayName = "same seats")]
    [DataRow(3, DisplayName = "increase")]
    public async Task CancelledClass_BlocksAnyChange(int seats)
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 2);
        await TestData.SetClassAsync(scheduledClass.Id, cancelled: true);

        await ResponseAssert.ConflictAsync(await user.Client.ChangeSeatsAsync(enrollmentId, seats), "class-cancelled");
        Assert.AreEqual(2, (await TestData.GetEnrollmentAsync(enrollmentId)).Seats);
    }

    [TestMethod]
    [DataRow(0, DisplayName = "class starting right now")]
    [DataRow(1, DisplayName = "class started a second ago")]
    public async Task StartedClass_BlocksAnyChange(int secondsAfterStart)
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(startsIn: TimeSpan.FromHours(1));
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 2);
        Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(secondsAfterStart));

        await ResponseAssert.ConflictAsync(await user.Client.ChangeSeatsAsync(enrollmentId, 1), "class-started");
    }

    [TestMethod]
    public async Task Change_IsAllowedOneSecondBeforeStart()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(startsIn: TimeSpan.FromHours(1));
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 2);
        Clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));

        await ResponseAssert.StatusAsync(await user.Client.ChangeSeatsAsync(enrollmentId, 3), HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task OtherUsersEnrollment_IsNotFound()
    {
        var owner = await TestData.CreateUserAsync();
        var stranger = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await EnrollAsync(owner, scheduledClass.Id, 2);

        await ResponseAssert.ProblemAsync(await stranger.Client.ChangeSeatsAsync(enrollmentId, 1), HttpStatusCode.NotFound);
        Assert.AreEqual(2, (await TestData.GetEnrollmentAsync(enrollmentId)).Seats);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(long.MaxValue)]
    public async Task UnknownEnrollment_IsNotFound(long id)
    {
        var user = await TestData.CreateUserAsync();

        await ResponseAssert.ProblemAsync(await user.Client.ChangeSeatsAsync(id, 1), HttpStatusCode.NotFound);
    }

    [TestMethod]
    [DataRow("abc", DisplayName = "letters")]
    [DataRow("1.5", DisplayName = "fraction")]
    [DataRow("9223372036854775808", DisplayName = "beyond long")]
    public async Task NonNumericId_IsBadRequest(string id)
    {
        var user = await TestData.CreateUserAsync();

        var response = await user.Client.PostJsonAsync($"/enrollments/{id}/seats", new { seats = 1 });

        await ResponseAssert.StatusAsync(response, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    [DataRow("""{"seats":0}""", DisplayName = "seats zero")]
    [DataRow("""{"seats":-3}""", DisplayName = "seats negative")]
    [DataRow("""{}""", DisplayName = "seats missing")]
    [DataRow("""{"seats":"2"x}""", DisplayName = "broken JSON")]
    [DataRow("""{"seats":"many"}""", DisplayName = "seats not a number")]
    [DataRow("""{"seats":2.5}""", DisplayName = "seats fractional")]
    [DataRow("""{"seats":"3"}""", DisplayName = "seats as numeric string")]
    [DataRow("", DisplayName = "empty body")]
    public async Task InvalidBody_IsBadRequest(string body)
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 2);

        var response = await user.Client.PostRawAsync($"/enrollments/{enrollmentId}/seats", body);

        await ResponseAssert.StatusAsync(response, HttpStatusCode.BadRequest);
        Assert.AreEqual(2, (await TestData.GetEnrollmentAsync(enrollmentId)).Seats);
    }

    [TestMethod]
    public async Task InvalidBody_IsReportedBeforeUnknownEnrollment()
    {
        var user = await TestData.CreateUserAsync();

        var response = await user.Client.PostRawAsync("/enrollments/999999999/seats", """{"seats":0}""");

        await ResponseAssert.StatusAsync(response, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task UnknownFields_AreIgnored()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await EnrollAsync(user, scheduledClass.Id, 2);

        var response = await user.Client.PostRawAsync(
            $"/enrollments/{enrollmentId}/seats",
            """{"seats":3,"delta":10,"classId":1}""");

        var card = await ResponseAssert.StatusAsync(response, HttpStatusCode.OK);
        Assert.AreEqual(3, card.GetProperty("seats").GetInt32());
    }

    [TestMethod]
    public async Task Change_RequiresAuthentication()
    {
        var response = await ApiClient.Anonymous().ChangeSeatsAsync(1, 1);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<long> EnrollAsync(TestUser user, long classId, int seats)
    {
        var json = await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(classId, seats), HttpStatusCode.Created);

        return json.GetProperty("id").GetInt64();
    }
}
