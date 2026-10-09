using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class IdempotencyRecordRepository : GenericRepository<IdempotencyRecord>, IIdempotencyRecordRepository
{
    public IdempotencyRecordRepository(DbSet<IdempotencyRecord> dbSet)
        : base(dbSet)
    {
    }

    public async Task<IdempotencyRecord?> FindAsync(
        long userId,
        string scope,
        string keyHash,
        CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Scope == scope && x.KeyHash == keyHash, ct);
    }
}
