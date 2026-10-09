using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using WebApi.Errors;
using WebApi.Infrastructure;

namespace WebApi.Services;

public class IdempotencyService
{
    public const string EnrollmentScope = "enrollments";
    public const string WaitlistScope = "enrollments/waitlist";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IdempotencyService> _logger;

    public IdempotencyService(IUnitOfWork unitOfWork, TimeProvider timeProvider, ILogger<IdempotencyService> logger)
    {
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<T?> FindReplayAsync<T>(IdempotencyRequest request, CancellationToken ct)
        where T : class
    {
        var record = await _unitOfWork.IdempotencyRecord.FindAsync(
            request.UserId,
            request.Scope,
            Hash(request.Key),
            ct);

        if (record == null)
        {
            return null;
        }

        // решение: тот же ключ с другим телом — не повтор, а ошибка клиента; отвечаем 409, а не
        // молча отдаём чужой результат.
        if (record.RequestHash != Hash(request.Fingerprint))
        {
            _logger.LogWarning(
                "User {UserId} reused idempotency key {KeyHash} on {Scope} with a different body",
                request.UserId,
                record.KeyHash[..12],
                request.Scope);

            throw ApiException.Conflict(ConflictReason.IdempotencyKeyConflict);
        }

        _logger.LogInformation(
            "Replaying stored response for user {UserId}, idempotency key {KeyHash} on {Scope}",
            request.UserId,
            record.KeyHash[..12],
            request.Scope);

        return JsonSerializer.Deserialize<T>(record.ResponseBody, SerializerOptions);
    }

    public async Task SaveAsync<T>(IdempotencyRequest request, T response, CancellationToken ct)
    {
        await _unitOfWork.IdempotencyRecord.CreateAsync(
            new IdempotencyRecord
            {
                UserId = request.UserId,
                Scope = request.Scope,
                KeyHash = Hash(request.Key),
                RequestHash = Hash(request.Fingerprint),
                ResponseBody = JsonSerializer.Serialize(response, SerializerOptions),
                CreatedAt = _timeProvider.GetUtcNowTruncated()
            },
            ct);
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

public record IdempotencyRequest(long UserId, string Scope, string Key, string Fingerprint);
