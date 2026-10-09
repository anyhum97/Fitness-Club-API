using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Endpoints;

[TestClass]
public class EnrollmentCardTests : ApiTestBase
{
    [TestMethod]
    public async Task Get_ReturnsEnrollmentWithClassRoomAndPass()
    {
        var user = await TestData.CreateUserAsync(visits: 10, validUntil: new DateOnly(2031, 5, 17));
        var room = await TestData.CreateRoomAsync();
        var scheduledClass = await TestData.CreateClassAsync(
            capacity: 12,
            roomId: room.Id,
            durationMinutes: 45,
            title: "Morning Yoga");
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 2);

        var json = await ResponseAssert.StatusAsync(await user.Client.GetCardAsync(enrollmentId), HttpStatusCode.OK);

        ResponseAssert.HasExactlyProperties(json, "id", "status", "seats", "createdAt", "class", "pass");
        Assert.AreEqual(enrollmentId, json.GetProperty("id").GetInt64());
        Assert.AreEqual("active", json.GetProperty("status").GetString());
        Assert.AreEqual(2, json.GetProperty("seats").GetInt32());
        Assert.AreEqual(Now, json.GetProperty("createdAt").GetDateTime());

        var classJson = json.GetProperty("class");
        ResponseAssert.HasExactlyProperties(classJson, "id", "title", "startsAt", "durationMinutes", "isCancelled", "room");
        Assert.AreEqual(scheduledClass.Id, classJson.GetProperty("id").GetInt64());
        Assert.AreEqual("Morning Yoga", classJson.GetProperty("title").GetString());
        Assert.AreEqual(scheduledClass.StartsAt, classJson.GetProperty("startsAt").GetDateTime());
        Assert.AreEqual(45, classJson.GetProperty("durationMinutes").GetInt32());
        Assert.IsFalse(classJson.GetProperty("isCancelled").GetBoolean());
        Assert.AreEqual(room.Id, classJson.GetProperty("room").GetProperty("id").GetInt64());
        Assert.AreEqual(room.Name, classJson.GetProperty("room").GetProperty("name").GetString());

        var passJson = json.GetProperty("pass");
        ResponseAssert.HasExactlyProperties(passJson, "remainingVisits", "validUntil");
        Assert.AreEqual(8, passJson.GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual("2031-05-17", passJson.GetProperty("validUntil").GetString());
    }

    [TestMethod]
    public async Task Get_DatesAreUtcIso8601()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 1);

        var json = await ResponseAssert.StatusAsync(await user.Client.GetCardAsync(enrollmentId), HttpStatusCode.OK);

        StringAssert.EndsWith(json.GetProperty("createdAt").GetString(), "Z");
        StringAssert.EndsWith(json.GetProperty("class").GetProperty("startsAt").GetString(), "Z");
    }

    [TestMethod]
    public async Task Get_ReturnsCancelledEnrollment()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 2);
        await user.CancelAsync(enrollmentId);

        var json = await ResponseAssert.StatusAsync(await user.Client.GetCardAsync(enrollmentId), HttpStatusCode.OK);

        Assert.AreEqual("cancelled", json.GetProperty("status").GetString());
        Assert.AreEqual(2, json.GetProperty("seats").GetInt32());
    }

    [TestMethod]
    public async Task Get_ReturnsEnrollmentOnClassCancelledByClub()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollment = await TestData.CreateEnrollmentAsync(user.Id, scheduledClass.Id, 1);
        await TestData.SetClassAsync(scheduledClass.Id, cancelled: true);

        var json = await ResponseAssert.StatusAsync(await user.Client.GetCardAsync(enrollment.Id), HttpStatusCode.OK);

        Assert.IsTrue(json.GetProperty("class").GetProperty("isCancelled").GetBoolean());
        Assert.AreEqual("active", json.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task Get_OtherUsersEnrollmentIsNotFound()
    {
        var owner = await TestData.CreateUserAsync();
        var stranger = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await owner.EnrollAsync(scheduledClass.Id, 1);

        await ResponseAssert.ProblemAsync(await stranger.Client.GetCardAsync(enrollmentId), HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Get_OtherUsersEnrollmentIsNotFoundEvenWhenCached()
    {
        var owner = await TestData.CreateUserAsync();
        var stranger = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await owner.EnrollAsync(scheduledClass.Id, 1);
        await ResponseAssert.StatusAsync(await owner.Client.GetCardAsync(enrollmentId), HttpStatusCode.OK);

        await ResponseAssert.ProblemAsync(await stranger.Client.GetCardAsync(enrollmentId), HttpStatusCode.NotFound);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(long.MaxValue)]
    public async Task Get_UnknownEnrollmentIsNotFound(long id)
    {
        var user = await TestData.CreateUserAsync();

        await ResponseAssert.ProblemAsync(await user.Client.GetCardAsync(id), HttpStatusCode.NotFound);
    }

    [TestMethod]
    [DataRow("abc")]
    [DataRow("12abc")]
    [DataRow("9223372036854775808")]
    [DataRow("waitlist")]
    public async Task Get_NonNumericIdIsBadRequest(string id)
    {
        var user = await TestData.CreateUserAsync();

        await ResponseAssert.StatusAsync(await user.Client.GetAsync($"/enrollments/{id}"), HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task Cache_ShowsNewSeatsAfterChange()
    {
        var user = await TestData.CreateUserAsync(visits: 10);
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 1);
        await ReadCardAsync(user, enrollmentId);

        await ResponseAssert.StatusAsync(await user.Client.ChangeSeatsAsync(enrollmentId, 4), HttpStatusCode.OK);

        var card = await ReadCardAsync(user, enrollmentId);
        Assert.AreEqual(4, card.GetProperty("seats").GetInt32());
        Assert.AreEqual(6, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
    }

    [TestMethod]
    public async Task Cache_ShowsCancellationAndRefund()
    {
        var user = await TestData.CreateUserAsync(visits: 10);
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 3);
        await ReadCardAsync(user, enrollmentId);

        await user.CancelAsync(enrollmentId);

        var card = await ReadCardAsync(user, enrollmentId);
        Assert.AreEqual("cancelled", card.GetProperty("status").GetString());
        Assert.AreEqual(10, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
    }

    [TestMethod]
    public async Task Cache_PassChangesFromAnotherEnrollmentAreVisible()
    {
        var user = await TestData.CreateUserAsync(visits: 10);
        var first = await TestData.CreateClassAsync();
        var second = await TestData.CreateClassAsync();
        var firstEnrollment = await user.EnrollAsync(first.Id, 1);
        Assert.AreEqual(9, RemainingVisits(await ReadCardAsync(user, firstEnrollment)));

        var secondEnrollment = await user.EnrollAsync(second.Id, 4);
        Assert.AreEqual(5, RemainingVisits(await ReadCardAsync(user, firstEnrollment)));

        await ResponseAssert.StatusAsync(await user.Client.ChangeSeatsAsync(secondEnrollment, 2), HttpStatusCode.OK);
        Assert.AreEqual(7, RemainingVisits(await ReadCardAsync(user, firstEnrollment)));

        await user.CancelAsync(secondEnrollment);
        Assert.AreEqual(9, RemainingVisits(await ReadCardAsync(user, firstEnrollment)));
    }

    [TestMethod]
    public async Task Cache_PromotionChargeIsVisibleOnWaitersOtherCard()
    {
        var holder = await TestData.CreateUserAsync(visits: 10);
        var waiter = await TestData.CreateUserAsync(visits: 10);
        var other = await TestData.CreateClassAsync();
        var waiterOtherEnrollment = await waiter.EnrollAsync(other.Id, 1);
        Assert.AreEqual(9, RemainingVisits(await ReadCardAsync(waiter, waiterOtherEnrollment)));

        var popular = await TestData.CreateClassAsync(capacity: 2);
        var holderEnrollment = await holder.EnrollAsync(popular.Id, 2);
        await waiter.JoinWaitlistAsync(popular.Id, 2);

        await holder.CancelAsync(holderEnrollment);

        Assert.AreEqual(7, RemainingVisits(await ReadCardAsync(waiter, waiterOtherEnrollment)));
    }

    [TestMethod]
    public async Task Get_IsServedRepeatedlyWithSameContent()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 1);

        var first = await ReadCardAsync(user, enrollmentId);
        var second = await ReadCardAsync(user, enrollmentId);

        Assert.AreEqual(first.GetRawText(), second.GetRawText());
    }

    [TestMethod]
    public async Task Get_RequiresAuthentication()
    {
        var response = await ApiClient.Anonymous().GetCardAsync(1);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<JsonElement> ReadCardAsync(TestUser user, long enrollmentId)
    {
        return await ResponseAssert.StatusAsync(await user.Client.GetCardAsync(enrollmentId), HttpStatusCode.OK);
    }

    private static int RemainingVisits(JsonElement card)
    {
        return card.GetProperty("pass").GetProperty("remainingVisits").GetInt32();
    }
}
