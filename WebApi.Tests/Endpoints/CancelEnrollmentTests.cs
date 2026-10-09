using System.Net;
using DataAccessLayer.Entities;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Endpoints;

[TestClass]
public class CancelEnrollmentTests : ApiTestBase
{
    [TestMethod]
    [DataRow(7200, 3, DisplayName = "exactly two hours before start")]
    [DataRow(7201, 3, DisplayName = "two hours and a second before start")]
    [DataRow(86400, 3, DisplayName = "a day before start")]
    [DataRow(7199, 0, DisplayName = "two hours minus a second before start")]
    [DataRow(600, 0, DisplayName = "ten minutes before start")]
    [DataRow(1, 0, DisplayName = "one second before start")]
    public async Task Cancel_RefundsDependingOnTimeLeft(int secondsBeforeStart, int expectedRefund)
    {
        var user = await TestData.CreateUserAsync(visits: 3);
        var scheduledClass = await TestData.CreateClassAsync(startsIn: TimeSpan.FromSeconds(secondsBeforeStart));
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 3);

        var json = await ResponseAssert.StatusAsync(await user.Client.CancelAsync(enrollmentId), HttpStatusCode.OK);

        ResponseAssert.HasExactlyProperties(json, "id", "status", "refundedVisits");
        Assert.AreEqual(enrollmentId, json.GetProperty("id").GetInt64());
        Assert.AreEqual("cancelled", json.GetProperty("status").GetString());
        Assert.AreEqual(expectedRefund, json.GetProperty("refundedVisits").GetInt32());
        Assert.AreEqual(expectedRefund, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
        Assert.AreEqual(0, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    public async Task Cancel_RefundsEvenWithExpiredPass()
    {
        var user = await TestData.CreateUserAsync(visits: 2);
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 2);
        await TestData.SetPassAsync(user.Id, validUntil: Clock.Today.AddDays(-3));

        Assert.AreEqual(2, await user.CancelAsync(enrollmentId));
        Assert.AreEqual(2, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    [DataRow(-86400, DisplayName = "club cancelled a class that started a day ago")]
    [DataRow(-1, DisplayName = "club cancelled a class that started a second ago")]
    [DataRow(0, DisplayName = "club cancelled a class starting right now")]
    [DataRow(60, DisplayName = "club cancelled a class starting in a minute")]
    [DataRow(86400, DisplayName = "club cancelled a class starting tomorrow")]
    public async Task Cancel_OnClassCancelledByClubRefundsFullyAtAnyTime(int secondsBeforeStart)
    {
        var user = await TestData.CreateUserAsync(visits: 5);
        var scheduledClass = await TestData.CreateClassAsync(startsIn: TimeSpan.FromDays(2));
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 2);
        await TestData.SetClassAsync(scheduledClass.Id, cancelled: true);
        Clock.Advance(TimeSpan.FromDays(2) - TimeSpan.FromSeconds(secondsBeforeStart));

        Assert.AreEqual(2, await user.CancelAsync(enrollmentId));
        Assert.AreEqual(5, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task ClubCancellation_DoesNotRefundByItself()
    {
        var user = await TestData.CreateUserAsync(visits: 5);
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 2);

        await TestData.SetClassAsync(scheduledClass.Id, cancelled: true);

        Assert.AreEqual(3, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
        Assert.AreEqual(EnrollmentStatus.Active, (await TestData.GetEnrollmentAsync(enrollmentId)).Status);
    }

    [TestMethod]
    [DataRow(0, DisplayName = "class starting right now")]
    [DataRow(1, DisplayName = "class started a second ago")]
    [DataRow(86400, DisplayName = "class started a day ago")]
    public async Task Cancel_RejectsStartedClassWhileEnrollmentIsActive(int secondsAfterStart)
    {
        var user = await TestData.CreateUserAsync(visits: 5);
        var scheduledClass = await TestData.CreateClassAsync(startsIn: TimeSpan.FromHours(3));
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 2);
        Clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromSeconds(secondsAfterStart));

        await ResponseAssert.ConflictAsync(await user.Client.CancelAsync(enrollmentId), "class-started");

        Assert.AreEqual(EnrollmentStatus.Active, (await TestData.GetEnrollmentAsync(enrollmentId)).Status);
        Assert.AreEqual(3, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    [DataRow(86400, 2, DisplayName = "repeat of a full refund")]
    [DataRow(600, 0, DisplayName = "repeat of a late cancellation")]
    public async Task Cancel_RepeatReturnsSameResultWithoutChanges(int secondsBeforeStart, int refund)
    {
        var user = await TestData.CreateUserAsync(visits: 2);
        var scheduledClass = await TestData.CreateClassAsync(startsIn: TimeSpan.FromSeconds(secondsBeforeStart));
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 2);

        var first = await ResponseAssert.StatusAsync(await user.Client.CancelAsync(enrollmentId), HttpStatusCode.OK);
        var second = await ResponseAssert.StatusAsync(await user.Client.CancelAsync(enrollmentId), HttpStatusCode.OK);

        Assert.AreEqual(first.GetRawText(), second.GetRawText());
        Assert.AreEqual(refund, second.GetProperty("refundedVisits").GetInt32());
        Assert.AreEqual(refund, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Cancel_RepeatAfterClassStartedStillReturnsSameResult()
    {
        var user = await TestData.CreateUserAsync(visits: 2);
        var scheduledClass = await TestData.CreateClassAsync(startsIn: TimeSpan.FromHours(5));
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 2);
        Assert.AreEqual(2, await user.CancelAsync(enrollmentId));

        Clock.Advance(TimeSpan.FromHours(6));

        Assert.AreEqual(2, await user.CancelAsync(enrollmentId));
        Assert.AreEqual(2, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Cancel_SeededCancelledEnrollmentReturnsStoredRefund()
    {
        var user = await TestData.CreateUserAsync(visits: 2);
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollment = await TestData.CreateEnrollmentAsync(
            user.Id,
            scheduledClass.Id,
            3,
            EnrollmentStatus.Cancelled,
            refundedVisits: 3);

        Assert.AreEqual(3, await user.CancelAsync(enrollment.Id));
        Assert.AreEqual(2, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Cancel_KeepsSeatsValueInCard()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 3);

        await user.CancelAsync(enrollmentId);

        var card = await ResponseAssert.StatusAsync(await user.Client.GetCardAsync(enrollmentId), HttpStatusCode.OK);
        Assert.AreEqual(3, card.GetProperty("seats").GetInt32());
        Assert.AreEqual("cancelled", card.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task Cancel_FreesSeatsForOthers()
    {
        var user = await TestData.CreateUserAsync();
        var other = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 2);
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 2);
        await ResponseAssert.ConflictAsync(await other.Client.EnrollAsync(scheduledClass.Id, 1), "capacity-exceeded");

        await user.CancelAsync(enrollmentId);

        await ResponseAssert.StatusAsync(await other.Client.EnrollAsync(scheduledClass.Id, 2), HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task Cancel_LateCancellationStillFreesSeats()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 2, startsIn: TimeSpan.FromMinutes(30));
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 2);

        Assert.AreEqual(0, await user.CancelAsync(enrollmentId));
        Assert.AreEqual(0, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    public async Task OtherUsersEnrollment_IsNotFound()
    {
        var owner = await TestData.CreateUserAsync();
        var stranger = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await owner.EnrollAsync(scheduledClass.Id, 1);

        await ResponseAssert.ProblemAsync(await stranger.Client.CancelAsync(enrollmentId), HttpStatusCode.NotFound);

        Assert.AreEqual(EnrollmentStatus.Active, (await TestData.GetEnrollmentAsync(enrollmentId)).Status);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-7L)]
    [DataRow(long.MaxValue)]
    public async Task UnknownEnrollment_IsNotFound(long id)
    {
        var user = await TestData.CreateUserAsync();

        await ResponseAssert.ProblemAsync(await user.Client.CancelAsync(id), HttpStatusCode.NotFound);
    }

    [TestMethod]
    [DataRow("abc")]
    [DataRow("1e3")]
    [DataRow("9223372036854775808")]
    public async Task NonNumericId_IsBadRequest(string id)
    {
        var user = await TestData.CreateUserAsync();

        await ResponseAssert.StatusAsync(await user.Client.PostAsync($"/enrollments/{id}/cancel"), HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task Cancel_IgnoresRequestBody()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 1);

        var response = await user.Client.PostRawAsync($"/enrollments/{enrollmentId}/cancel", """{"refund":100}""");

        var json = await ResponseAssert.StatusAsync(response, HttpStatusCode.OK);
        Assert.AreEqual(1, json.GetProperty("refundedVisits").GetInt32());
    }

    [TestMethod]
    public async Task Cancel_RequiresAuthentication()
    {
        var response = await ApiClient.Anonymous().CancelAsync(1);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
