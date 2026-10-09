using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace WebApi.Tests.Infrastructure;

public sealed class ApiClient
{
    private readonly HttpClient _http;

    private ApiClient(HttpClient http)
    {
        _http = http;
    }

    public static ApiClient WithToken(string? token)
    {
        var http = TestApp.Factory.CreateClient();

        if (token != null)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return new ApiClient(http);
    }

    public static ApiClient Anonymous()
    {
        return WithToken(null);
    }

    public Task<HttpResponseMessage> EnrollAsync(long classId, int seats, string? idempotencyKey = null)
    {
        return PostJsonAsync("/enrollments", new { classId, seats }, idempotencyKey ?? NewKey());
    }

    public Task<HttpResponseMessage> ChangeSeatsAsync(long enrollmentId, int seats)
    {
        return PostJsonAsync($"/enrollments/{enrollmentId}/seats", new { seats });
    }

    public Task<HttpResponseMessage> CancelAsync(long enrollmentId)
    {
        return PostAsync($"/enrollments/{enrollmentId}/cancel");
    }

    public Task<HttpResponseMessage> GetCardAsync(long enrollmentId)
    {
        return GetAsync($"/enrollments/{enrollmentId}");
    }

    public Task<HttpResponseMessage> JoinWaitlistAsync(long classId, int seats, string? idempotencyKey = null)
    {
        return PostJsonAsync("/enrollments/waitlist", new { classId, seats }, idempotencyKey ?? NewKey());
    }

    public Task<HttpResponseMessage> LeaveWaitlistAsync(long entryId)
    {
        return PostAsync($"/enrollments/waitlist/{entryId}/cancel");
    }

    public Task<HttpResponseMessage> GetAsync(string url)
    {
        return _http.GetAsync(url);
    }

    public Task<HttpResponseMessage> PostAsync(string url)
    {
        return _http.PostAsync(url, null);
    }

    public Task<HttpResponseMessage> PostJsonAsync(string url, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };

        if (idempotencyKey != null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return _http.SendAsync(request);
    }

    public Task<HttpResponseMessage> PostRawAsync(
        string url,
        string? content,
        string? idempotencyKey = null,
        string contentType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);

        if (content != null)
        {
            request.Content = new StringContent(content, Encoding.UTF8, contentType);
        }

        if (idempotencyKey != null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return _http.SendAsync(request);
    }

    public static string NewKey()
    {
        return Guid.NewGuid().ToString("N");
    }
}
