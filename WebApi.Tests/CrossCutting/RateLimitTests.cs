using System.Net;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.CrossCutting;

[TestClass]
public class RateLimitTests : ApiTestBase
{
    private const string ScheduleUrl = "/schedule?from=2034-01-01&to=2034-01-02&roomId=0";

    [TestMethod]
    public async Task SixtyFirstRequestWithinMinuteIsRejected()
    {
        var user = await TestData.CreateUserAsync();

        for (var i = 0; i < 60; i++)
        {
            Assert.AreEqual(HttpStatusCode.OK, (await user.Client.GetAsync(ScheduleUrl)).StatusCode, $"request {i + 1}");
        }

        var rejected = await user.Client.GetAsync(ScheduleUrl);

        await ResponseAssert.ProblemAsync(rejected, HttpStatusCode.TooManyRequests);
        Assert.IsTrue(rejected.Headers.Contains("Retry-After"));
    }

    [TestMethod]
    public async Task LimitIsSharedAcrossAllEndpoints()
    {
        var user = await TestData.CreateUserAsync(visits: 100);
        var scheduledClass = await TestData.CreateClassAsync(capacity: 100);
        var enrollmentId = await user.EnrollAsync(scheduledClass.Id, 1);

        for (var i = 1; i < 60; i++)
        {
            var response = (i % 4) switch
            {
                0 => await user.Client.GetAsync(ScheduleUrl),
                1 => await user.Client.GetCardAsync(enrollmentId),
                2 => await user.Client.ChangeSeatsAsync(enrollmentId, 1 + i % 2),
                _ => await user.Client.LeaveWaitlistAsync(long.MaxValue)
            };

            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, response.StatusCode, $"request {i + 1}");
        }

        await ResponseAssert.ProblemAsync(await user.Client.CancelAsync(enrollmentId), HttpStatusCode.TooManyRequests);
        await ResponseAssert.ProblemAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1), HttpStatusCode.TooManyRequests);
        await ResponseAssert.ProblemAsync(await user.Client.JoinWaitlistAsync(scheduledClass.Id, 1), HttpStatusCode.TooManyRequests);
    }

    [TestMethod]
    public async Task RejectedRequestLeavesNoTrace()
    {
        var user = await TestData.CreateUserAsync(visits: 10);
        var scheduledClass = await TestData.CreateClassAsync();
        await ExhaustLimitAsync(user);

        await ResponseAssert.ProblemAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1), HttpStatusCode.TooManyRequests);

        Assert.AreEqual(10, (await TestData.GetPassAsync(user.Id)).RemainingVisits);
        Assert.AreEqual(0, await TestData.GetActiveSeatsAsync(scheduledClass.Id));
    }

    [TestMethod]
    public async Task FailedRequestsCountTowardsLimit()
    {
        var user = await TestData.CreateUserAsync();

        for (var i = 0; i < 60; i++)
        {
            await user.Client.GetAsync("/schedule?from=bad");
        }

        await ResponseAssert.ProblemAsync(await user.Client.GetAsync(ScheduleUrl), HttpStatusCode.TooManyRequests);
    }

    [TestMethod]
    public async Task LimitIsPerUser()
    {
        var first = await TestData.CreateUserAsync();
        var second = await TestData.CreateUserAsync();
        await ExhaustLimitAsync(first);

        Assert.AreEqual(HttpStatusCode.OK, (await second.Client.GetAsync(ScheduleUrl)).StatusCode);
    }

    [TestMethod]
    public async Task LimitResetsInNextMinute()
    {
        var user = await TestData.CreateUserAsync();
        await ExhaustLimitAsync(user);

        Clock.Advance(TimeSpan.FromSeconds(60));

        Assert.AreEqual(HttpStatusCode.OK, (await user.Client.GetAsync(ScheduleUrl)).StatusCode);
    }

    [TestMethod]
    public async Task UnauthenticatedRequestsAreNotCounted()
    {
        var anonymous = ApiClient.Anonymous();

        for (var i = 0; i < 70; i++)
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(ScheduleUrl)).StatusCode);
        }
    }

    [TestMethod]
    public async Task LoginIsNotRateLimited()
    {
        var anonymous = ApiClient.Anonymous();

        for (var i = 0; i < 65; i++)
        {
            var response = await anonymous.PostJsonAsync("/auth/login", new { email = "nobody@club.test", password = "x" });

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private static async Task ExhaustLimitAsync(TestUser user)
    {
        for (var i = 0; i < 60; i++)
        {
            await user.Client.GetAsync(ScheduleUrl);
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, (await user.Client.GetAsync(ScheduleUrl)).StatusCode);
    }
}
