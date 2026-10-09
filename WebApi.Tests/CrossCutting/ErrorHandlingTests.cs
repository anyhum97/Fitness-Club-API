using System.Net;
using System.Net.Http.Headers;
using DataAccessLayer.Models;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Serilog.Events;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.CrossCutting;

[TestClass]
public class ErrorHandlingTests : ApiTestBase
{
    private const string SecretMessage = "connection string Password=club leaked";

    private static WebApplicationFactory<Program>? _failingFactory;

    [ClassCleanup]
    public static async Task DisposeFailingFactory()
    {
        if (_failingFactory != null)
        {
            await _failingFactory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task UnhandledException_ReturnsGeneric500WithoutDetails()
    {
        var response = await SendFailingRequestAsync();

        var body = await response.Content.ReadAsStringAsync();
        var json = await ResponseAssert.StatusAsync(response, HttpStatusCode.InternalServerError);

        ResponseAssert.HasExactlyProperties(json, "type", "title", "status", "traceId");
        Assert.AreEqual(500, json.GetProperty("status").GetInt32());
        Assert.AreEqual("Internal server error", json.GetProperty("title").GetString());
        StringAssert.StartsWith(response.Content.Headers.ContentType!.ToString(), "application/problem+json");
        Assert.IsFalse(body.Contains(SecretMessage));
        Assert.IsFalse(body.Contains("InvalidOperationException"));
        Assert.IsFalse(body.Contains(" at "));
        Assert.IsFalse(body.Contains(".cs:line"));
    }

    [TestMethod]
    public async Task UnhandledException_TraceIdInBodyMatchesHeader()
    {
        var response = await SendFailingRequestAsync();

        var json = await ResponseAssert.StatusAsync(response, HttpStatusCode.InternalServerError);
        var traceId = json.GetProperty("traceId").GetString();

        Assert.IsFalse(string.IsNullOrWhiteSpace(traceId));
        Assert.AreEqual(traceId, response.Headers.GetValues("X-Trace-Id").Single());
    }

    [TestMethod]
    public async Task UnhandledException_IsLoggedAsErrorWithStackTraceUnderSameTraceId()
    {
        var user = await TestData.CreateUserAsync();
        var response = await SendFailingRequestAsync(user);
        var traceId = (await ResponseAssert.StatusAsync(response, HttpStatusCode.InternalServerError))
            .GetProperty("traceId")
            .GetString()!;

        var errors = TestApp.Factory.LogSink.WithTraceId(traceId)
            .Where(x => x.Level == LogEventLevel.Error && x.Exception != null)
            .ToList();

        Assert.AreEqual(1, errors.Count);
        var entry = errors[0];
        Assert.IsInstanceOfType<InvalidOperationException>(entry.Exception);
        Assert.AreEqual(SecretMessage, entry.Exception!.Message);
        Assert.IsNotNull(entry.Exception.StackTrace);
        Assert.AreEqual(user.Id.ToString(), entry.Properties["UserId"].ToString().Trim('"'));
        Assert.AreEqual("\"GET\"", entry.Properties["Method"].ToString());
        Assert.AreEqual("\"/schedule\"", entry.Properties["Path"].ToString());
    }

    [TestMethod]
    public async Task UnhandledException_DifferentRequestsGetDifferentTraceIds()
    {
        var first = await ResponseAssert.StatusAsync(await SendFailingRequestAsync(), HttpStatusCode.InternalServerError);
        var second = await ResponseAssert.StatusAsync(await SendFailingRequestAsync(), HttpStatusCode.InternalServerError);

        Assert.AreNotEqual(first.GetProperty("traceId").GetString(), second.GetProperty("traceId").GetString());
    }

    [TestMethod]
    public async Task UnhandledException_DoesNotBreakFollowingRequests()
    {
        await SendFailingRequestAsync();

        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();

        await ResponseAssert.StatusAsync(await user.Client.EnrollAsync(scheduledClass.Id, 1), HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task TraceIdHeader_IsPresentOnSuccessfulResponses()
    {
        var user = await TestData.CreateUserAsync();

        var response = await user.Client.GetAsync("/schedule?from=2034-01-01&to=2034-01-02&roomId=0");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(32, response.Headers.GetValues("X-Trace-Id").Single().Length);
    }

    [TestMethod]
    public async Task TraceIdHeader_IsPresentOnUnauthorizedResponses()
    {
        var response = await ApiClient.Anonymous().GetAsync("/schedule?from=2034-01-01&to=2034-01-02");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsTrue(response.Headers.Contains("X-Trace-Id"));
    }

    [TestMethod]
    public async Task ConflictAndValidationResponses_CarryTraceIdMatchingHeader()
    {
        var user = await TestData.CreateUserAsync();
        var full = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(full.Id, 1);

        foreach (var response in new[]
                 {
                     await user.Client.EnrollAsync(full.Id, 1),
                     await user.Client.GetAsync("/enrollments/abc"),
                     await user.Client.GetAsync("/schedule?from=bad&to=bad"),
                     await user.Client.GetCardAsync(long.MaxValue)
                 })
        {
            var json = await ResponseAssert.StatusAsync(response, response.StatusCode);

            Assert.AreEqual(
                response.Headers.GetValues("X-Trace-Id").Single(),
                json.GetProperty("traceId").GetString(),
                response.RequestMessage!.RequestUri!.ToString());
        }
    }

    [TestMethod]
    public async Task Conflict_IsLoggedAsWarningWithReason()
    {
        var user = await TestData.CreateUserAsync();
        var full = await TestData.CreateClassAsync(capacity: 1);
        await TestData.FillClassAsync(full.Id, 1);

        var response = await user.Client.EnrollAsync(full.Id, 1);
        var traceId = response.Headers.GetValues("X-Trace-Id").Single();

        var warning = TestApp.Factory.LogSink.WithTraceId(traceId).Single(x => x.Level == LogEventLevel.Warning);
        Assert.AreEqual("\"capacity-exceeded\"", warning.Properties["Reason"].ToString());
    }

    [TestMethod]
    public async Task BusinessEvents_AreLoggedAtInformationWithUserId()
    {
        var user = await TestData.CreateUserAsync();
        var scheduledClass = await TestData.CreateClassAsync();

        var response = await user.Client.EnrollAsync(scheduledClass.Id, 2);
        var traceId = response.Headers.GetValues("X-Trace-Id").Single();

        var events = TestApp.Factory.LogSink.WithTraceId(traceId);
        var created = events.Single(x =>
            x.Level == LogEventLevel.Information && x.MessageTemplate.Text.StartsWith("Enrollment {EnrollmentId} on class"));
        Assert.AreEqual(user.Id.ToString(), created.Properties["UserId"].ToString().Trim('"'));
        Assert.IsTrue(events.Any(x => x.Level == LogEventLevel.Debug));
        Assert.IsTrue(events.Any(x => x.MessageTemplate.Text.StartsWith("HTTP")));
    }

    [TestMethod]
    public async Task RateLimitRejection_IsLoggedAsWarning()
    {
        var user = await TestData.CreateUserAsync();

        for (var i = 0; i < 60; i++)
        {
            await user.Client.GetAsync("/schedule?from=2034-01-01&to=2034-01-02&roomId=0");
        }

        var rejected = await user.Client.GetAsync("/schedule?from=2034-01-01&to=2034-01-02&roomId=0");
        var traceId = rejected.Headers.GetValues("X-Trace-Id").Single();

        Assert.IsTrue(TestApp.Factory.LogSink.WithTraceId(traceId).Any(x =>
            x.Level == LogEventLevel.Warning && x.MessageTemplate.Text.StartsWith("Rate limit exceeded")));
    }

    private static async Task<HttpResponseMessage> SendFailingRequestAsync(TestUser? user = null)
    {
        user ??= await TestData.CreateUserAsync();
        var client = GetFailingFactory().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestApp.Services.GetRequiredService<WebApi.Auth.AccessTokenService>().Create(user.User).AccessToken);

        return await client.GetAsync("/schedule?from=2034-01-01&to=2034-01-02");
    }

    private static WebApplicationFactory<Program> GetFailingFactory()
    {
        return _failingFactory ??= TestApp.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                var classes = new Mock<IClassRepository>();
                classes
                    .Setup(x => x.GetScheduleAsync(
                        It.IsAny<DateTime>(),
                        It.IsAny<DateTime>(),
                        It.IsAny<long?>(),
                        It.IsAny<ScheduleCursor?>(),
                        It.IsAny<int>(),
                        It.IsAny<long>(),
                        It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException(SecretMessage));

                var unitOfWork = new Mock<IUnitOfWork>();
                unitOfWork.Setup(x => x.Class).Returns(classes.Object);

                services.RemoveAll<IUnitOfWork>();
                services.AddScoped(_ => unitOfWork.Object);
            }));
    }
}
