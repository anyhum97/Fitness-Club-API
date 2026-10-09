using System.Net;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.CrossCutting;

[TestClass]
public class IdempotencyTests : ApiTestBase
{
    [TestMethod]
    public async Task Repeat_ReturnsCreatedWithFirstBodyAndChargesOnce()
    {
        var user = await TestData.CreateUserAsync(visits: 10);
        var scheduledClass = await TestData.CreateClassAsync();
        var key = ApiClient.NewKey();

        var first = await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 2, key), HttpStatusCode.Created);
        Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 2, key), HttpStatusCode.Created);

        Assert.AreEqual(first.GetRawText(), second.GetRawText());
        Assert.AreEqual(1, (await TestData.GetEnrollmentsAsync(scheduledClass.Id)).Count);
        Assert.AreEqual(8, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Repeat_ReturnsFirstBodyEvenAfterEnrollmentChanged()
    {
        var user = await TestData.CreateUserAsync(visits: 10);
        var scheduledClass = await TestData.CreateClassAsync();
        var key = ApiClient.NewKey();
        var first = await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 2, key), HttpStatusCode.Created);
        await user.CancelAsync(first.GetProperty("id").GetInt64());

        var second = await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 2, key), HttpStatusCode.Created);

        Assert.AreEqual(first.GetRawText(), second.GetRawText());
        Assert.AreEqual(10, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Repeat_ReturnsFirstBodyEvenAfterClassStartedOrFilled()
    {
        var user = await TestData.CreateUserAsync(visits: 10);
        var scheduledClass = await TestData.CreateClassAsync(capacity: 2, startsIn: TimeSpan.FromHours(1));
        var key = ApiClient.NewKey();
        var first = await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1, key), HttpStatusCode.Created);
        await TestData.FillClassAsync(scheduledClass.Id, 1);
        Clock.Advance(TimeSpan.FromHours(2));

        var second = await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1, key), HttpStatusCode.Created);

        Assert.AreEqual(first.GetRawText(), second.GetRawText());
    }

    [TestMethod]
    [DataRow(2, DisplayName = "different seats")]
    [DataRow(-1, DisplayName = "different class")]
    public async Task SameKeyWithDifferentBody_IsConflict(int seats)
    {
        var user = await TestData.CreateUserAsync(visits: 10);
        var scheduledClass = await TestData.CreateClassAsync();
        var otherClass = await TestData.CreateClassAsync();
        var key = ApiClient.NewKey();
        await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1, key), HttpStatusCode.Created);

        var response = seats > 0
            ? await user.Client.EnrollAsync(scheduledClass.Id, seats, key)
            : await user.Client.EnrollAsync(otherClass.Id, 1, key);

        await ResponseAssert.ConflictAsync(response, "idempotency-key-conflict");
        Assert.AreEqual(9, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task SameBodyWithDifferentFieldOrderAndExtraFields_IsRepeat()
    {
        var user = await TestData.CreateUserAsync(visits: 10);
        var scheduledClass = await TestData.CreateClassAsync();
        var key = ApiClient.NewKey();
        var first = await ResponseAssert.StatusAsync(
            await user.Client.PostRawAsync("/enrollments", $$"""{"classId":{{scheduledClass.Id}},"seats":1}""", key),
            HttpStatusCode.Created);

        var second = await ResponseAssert.StatusAsync(
            await user.Client.PostRawAsync(
                "/enrollments",
                $$"""{ "seats": 1, "note": "again", "classId": {{scheduledClass.Id}} }""",
                key),
            HttpStatusCode.Created);

        Assert.AreEqual(first.GetRawText(), second.GetRawText());
    }

    [TestMethod]
    public async Task KeysAreScopedPerUser()
    {
        var first = await TestData.CreateUserAsync();
        var second = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();
        var key = ApiClient.NewKey();

        var firstJson = await ResponseAssert.StatusAsync(await first.Client.EnrollAsync(scheduledClass.Id, 1, key), HttpStatusCode.Created);
        var secondJson = await ResponseAssert.StatusAsync(await second.Client.EnrollAsync(scheduledClass.Id, 1, key), HttpStatusCode.Created);

        Assert.AreNotEqual(firstJson.GetProperty("id").GetInt64(), secondJson.GetProperty("id").GetInt64());
        Assert.AreEqual(2, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    public async Task KeysAreCaseSensitive()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();

        await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1, "Key-A"), HttpStatusCode.Created);
        await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1, "key-a"), HttpStatusCode.Created);

        Assert.AreEqual(2, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    [DataRow("pass-exhausted")]
    [DataRow("capacity-exceeded")]
    [DataRow("not-found")]
    public async Task FailedRequestIsNotStoredAndCanBeRetriedWithSameKey(string failure)
    {
        var user = await TestData.CreateUserAsync(visits: failure == "pass-exhausted" ? 0 : 5);
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        var key = ApiClient.NewKey();

        if (failure == "capacity-exceeded")
        {
            await TestData.FillClassAsync(scheduledClass.Id, 1);
        }

        var classId = failure == "not-found" ? long.MaxValue : scheduledClass.Id;
        var failed = await user.Client.EnrollAsync(classId, 1, key);
        Assert.AreNotEqual(HttpStatusCode.Created, failed.StatusCode);
        Assert.AreEqual(0, await TestData.CountIdempotencyRecordsAsync(user.Id));

        if (failure == "pass-exhausted")
        {
            await TestData.SetPassAsync(user.Id, visits: 1);
        }

        if (failure == "capacity-exceeded")
        {
            await TestData.SetClassAsync(scheduledClass.Id, capacity: 2);
        }

        await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1, key), HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task ParallelRequestsWithSameKey_CreateOneEnrollment()
    {
        var user = await TestData.CreateUserAsync(visits: 30);
        var scheduledClass = await TestData.CreateClassAsync(capacity: 30);
        var key = ApiClient.NewKey();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(_ => user.Client.EnrollAsync(scheduledClass.Id, 2, key)));

        var bodies = new List<string>();

        foreach (var response in responses)
        {
            bodies.Add((await ResponseAssert.StatusAsync(response, HttpStatusCode.Created)).GetRawText());
        }

        Assert.AreEqual(1, bodies.Distinct().Count());
        Assert.AreEqual(1, (await TestData.GetEnrollmentsAsync(scheduledClass.Id)).Count);
        Assert.AreEqual(28, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task ParallelRequestsWithSameKeyOnDifferentClasses_OneWinsOthersConflict()
    {
        var user = await TestData.CreateUserAsync(visits: 30);
        var classes = new List<long>();

        for (var i = 0; i < 6; i++)
        {
            classes.Add((await TestData.CreateClassAsync()).Id);
        }

        var key = ApiClient.NewKey();

        var responses = await Task.WhenAll(classes.Select(classId => user.Client.EnrollAsync(classId, 1, key)));

        Assert.AreEqual(1, responses.Count(x => x.StatusCode == HttpStatusCode.Created));

        foreach (var response in responses.Where(x => x.StatusCode != HttpStatusCode.Created))
        {
            await ResponseAssert.ConflictAsync(response, "idempotency-key-conflict");
        }

        Assert.AreEqual(29, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task ParallelWaitlistJoinsWithSameKey_CreateOneEntry()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(scheduledClass.Id, 1);
        var key = ApiClient.NewKey();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => user.Client.JoinWaitlistAsync(scheduledClass.Id, 1, key)));

        foreach (var response in responses)
        {
            await ResponseAssert.StatusAsync(response, HttpStatusCode.Created);
        }

        Assert.AreEqual(1, (await TestData.GetWaitlistAsync(scheduledClass.Id)).Count);
    }
}
