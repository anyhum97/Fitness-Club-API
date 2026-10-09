using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using WebApi.Auth;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.CrossCutting;

[TestClass]
public class AuthenticationTests : ApiTestBase
{
    public static IEnumerable<object[]> Endpoints =>
    [
        ["POST", "/enrollments", """{"classId":1,"seats":1}"""],
        ["POST", "/enrollments/1/seats", """{"seats":1}"""],
        ["POST", "/enrollments/1/cancel", null!],
        ["GET", "/enrollments/1", null!],
        ["GET", "/schedule?from=2034-01-01&to=2034-01-02", null!],
        ["GET", "/analytics/load?from=2034-01-01&to=2034-01-02", null!],
        ["POST", "/enrollments/waitlist", """{"classId":1,"seats":1}"""],
        ["POST", "/enrollments/waitlist/1/cancel", null!]
    ];

    [TestMethod]
    [DynamicData(nameof(Endpoints))]
    public async Task MissingToken_IsUnauthorized(string method, string url, string? body)
    {
        var response = await SendAsync(null, method, url, body);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [DynamicData(nameof(Endpoints))]
    public async Task MalformedToken_IsUnauthorized(string method, string url, string? body)
    {
        var response = await SendAsync("not-a-jwt", method, url, body);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [DynamicData(nameof(Endpoints))]
    public async Task ExpiredToken_IsUnauthorized(string method, string url, string? body)
    {
        var token = CreateToken(DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddMinutes(-1), SigningKey());

        var response = await SendAsync(token, method, url, body);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [DynamicData(nameof(Endpoints))]
    public async Task TokenSignedWithOtherKey_IsUnauthorized(string method, string url, string? body)
    {
        var token = CreateToken(DateTime.UtcNow, DateTime.UtcNow.AddHours(1), new string('x', 64));

        var response = await SendAsync(token, method, url, body);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task ValidTokenFromLogin_Works()
    {
        var login = await ApiClient.Anonymous().PostJsonAsync(
            "/auth/login",
            new { email = "user001@club.test", password = "Password123!" });
        var json = await ResponseAssert.StatusAsync(login, HttpStatusCode.OK);

        var response = await ApiClient
            .WithToken(json.GetProperty("accessToken").GetString())
            .GetAsync("/schedule?from=2034-01-01&to=2034-01-02&roomId=0");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task AdminCanUseUserEndpoints()
    {
        var admin = await TestData.CreateUserAsync(role: "admin");
        var scheduledClass = await TestData.CreateClassAsync();

        await ResponseAssert.StatusAsync(await admin.Client.EnrollAsync(scheduledClass.Id, 1), HttpStatusCode.Created);
    }

    private static async Task<HttpResponseMessage> SendAsync(string? token, string method, string url, string? body)
    {
        var client = ApiClient.WithToken(token);

        return method == "GET"
            ? await client.GetAsync(url)
            : await client.PostRawAsync(url, body, ApiClient.NewKey());
    }

    private static string SigningKey()
    {
        return TestApp.Services.GetRequiredService<JwtOptions>().Key;
    }

    private static string CreateToken(DateTime notBefore, DateTime expires, string key)
    {
        var options = TestApp.Services.GetRequiredService<JwtOptions>();
        var token = new JwtSecurityToken(
            options.Issuer,
            options.Audience,
            [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, "admin")],
            notBefore,
            expires,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
