using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IWaitlistRepository : IGenericRepository<WaitlistEntry>
{
    Task<WaitlistEntry?> GetOwnedAsync(long id, long userId, CancellationToken ct = default);

    Task<bool> IsWaitingAsync(long userId, long classId, CancellationToken ct = default);

    Task<List<WaitlistEntry>> GetWaitingInQueueOrderAsync(long classId, CancellationToken ct = default);

    Task<int> GetPositionAsync(WaitlistEntry entry, CancellationToken ct = default);
}
