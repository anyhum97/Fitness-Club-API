using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IIdempotencyRecordRepository : IGenericRepository<IdempotencyRecord>
{
    Task<IdempotencyRecord?> FindAsync(long userId, string scope, string keyHash, CancellationToken ct = default);
}
