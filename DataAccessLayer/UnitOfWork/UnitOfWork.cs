using Common.Database.UnitOfWork;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DataAccessLayer.UnitOfWork;

public interface IUnitOfWork : IUnitOfWorkBase
{
    IRoomRepository Room { get; }

    IClassRepository Class { get; }

    IEnrollmentRepository Enrollment { get; }

    IPassRepository Pass { get; }

    IWaitlistRepository Waitlist { get; }

    IIdempotencyRecordRepository IdempotencyRecord { get; }

    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default);
}

public class UnitOfWork : UnitOfWorkBase, IUnitOfWork
{
    private const int MaxTransactionAttempts = 3;

    private static readonly string[] RetryableSqlStates =
    [
        PostgresErrorCodes.DeadlockDetected,
        PostgresErrorCodes.SerializationFailure
    ];

    private readonly ILogger<UnitOfWork> _logger;

    public UnitOfWork(ClubDbContext dbContext, ILogger<UnitOfWork> logger)
        : base(dbContext)
    {
        _logger = logger;
    }

    public IRoomRepository Room => GetRepository<Room, IRoomRepository>(set => new RoomRepository(set));

    public IClassRepository Class => GetRepository<Class, IClassRepository>(set => new ClassRepository(set));

    public IEnrollmentRepository Enrollment =>
        GetRepository<Enrollment, IEnrollmentRepository>(set => new EnrollmentRepository(set));

    public IPassRepository Pass => GetRepository<Pass, IPassRepository>(set => new PassRepository(set));

    public IWaitlistRepository Waitlist =>
        GetRepository<WaitlistEntry, IWaitlistRepository>(set => new WaitlistRepository(set));

    public IIdempotencyRecordRepository IdempotencyRecord =>
        GetRepository<IdempotencyRecord, IIdempotencyRecordRepository>(set => new IdempotencyRecordRepository(set));

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var transaction = await DbContext.Database.BeginTransactionAsync(ct);

            try
            {
                var result = await operation(ct);

                await DbContext.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return result;
            }
            catch (Exception exception) when (attempt < MaxTransactionAttempts && IsRetryable(exception))
            {
                _logger.LogWarning(
                    exception,
                    "Transaction attempt {Attempt} of {MaxAttempts} failed with {SqlState}, retrying",
                    attempt,
                    MaxTransactionAttempts,
                    FindPostgresException(exception)?.SqlState);

                DbContext.ChangeTracker.Clear();
            }
            catch
            {
                DbContext.ChangeTracker.Clear();
                throw;
            }
        }
    }

    public static bool IsUniqueViolation(Exception exception, string constraintName)
    {
        return FindPostgresException(exception) is { SqlState: PostgresErrorCodes.UniqueViolation } postgres
            && postgres.ConstraintName == constraintName;
    }

    private static bool IsRetryable(Exception exception)
    {
        return FindPostgresException(exception) is { } postgres && RetryableSqlStates.Contains(postgres.SqlState);
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}
