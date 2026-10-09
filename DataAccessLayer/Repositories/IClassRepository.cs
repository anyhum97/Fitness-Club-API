using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using DataAccessLayer.Models;

namespace DataAccessLayer.Repositories;

public interface IClassRepository : IGenericRepository<Class>
{
    Task<Class?> GetWithEnrollmentsAsync(long id, CancellationToken ct = default);

    Task<Class?> GetForUpdateAsync(long id, CancellationToken ct = default);

    Task<List<ScheduleRow>> GetScheduleAsync(
        DateTime from,
        DateTime to,
        long? roomId,
        ScheduleCursor? after,
        int take,
        long userId,
        CancellationToken ct = default);

    Task<List<RoomLoadRow>> GetRoomLoadAsync(
        DateOnly from,
        DateOnly to,
        long? roomId,
        CancellationToken ct = default);
}
