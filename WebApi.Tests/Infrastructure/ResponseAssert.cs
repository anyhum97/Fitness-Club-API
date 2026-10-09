using System.Net;
using System.Text.Json;

namespace WebApi.Tests.Infrastructure;

public static class ResponseAssert
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task<JsonElement> StatusAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(
            expected,
            response.StatusCode,
            $"Unexpected status for {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");

        return string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    public static async Task<T> OkAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var json = await StatusAsync(response, expected);

        return json.Deserialize<T>(SerializerOptions)!;
    }

    public static async Task ConflictAsync(HttpResponseMessage response, string reason)
    {
        var json = await StatusAsync(response, HttpStatusCode.Conflict);

        Assert.AreEqual(reason, json.GetProperty("reason").GetString());
        Assert.AreEqual(409, json.GetProperty("status").GetInt32());
        Assert.IsTrue(json.TryGetProperty("title", out _));
        StringAssert.StartsWith(
            response.Content.Headers.ContentType?.ToString() ?? string.Empty,
            "application/problem+json");
    }

    public static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var json = await StatusAsync(response, expected);

        Assert.AreEqual((int)expected, json.GetProperty("status").GetInt32());
        Assert.IsFalse(json.TryGetProperty("reason", out _));
    }

    public static void HasExactlyProperties(JsonElement json, params string[] names)
    {
        var actual = json.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray();

        CollectionAssert.AreEqual(names.OrderBy(x => x).ToArray(), actual, string.Join(", ", actual));
    }
}
