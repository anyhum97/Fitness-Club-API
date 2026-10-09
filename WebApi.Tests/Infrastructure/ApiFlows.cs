using System.Net;

namespace WebApi.Tests.Infrastructure;

public static class ApiFlows
{
    public static async Task<long> EnrollAsync(this TestUser user, long classId, int seats)
    {
        var json = await ResponseAssert.StatusAsync(
            await user.Client.EnrollAsync(classId, seats),
            HttpStatusCode.Created);

        return json.GetProperty("id").GetInt64();
    }

    public static async Task<long> JoinWaitlistAsync(this TestUser user, long classId, int seats)
    {
        var json = await ResponseAssert.StatusAsync(
            await user.Client.JoinWaitlistAsync(classId, seats),
            HttpStatusCode.Created);

        return json.GetProperty("id").GetInt64();
    }

    public static async Task<int> CancelAsync(this TestUser user, long enrollmentId)
    {
        var json = await ResponseAssert.StatusAsync(await user.Client.CancelAsync(enrollmentId), HttpStatusCode.OK);

        return json.GetProperty("refundedVisits").GetInt32();
    }
}
