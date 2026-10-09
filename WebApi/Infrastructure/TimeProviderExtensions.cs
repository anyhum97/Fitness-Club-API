namespace WebApi.Infrastructure;

public static class TimeProviderExtensions
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    public static DateTime GetUtcNowTruncated(this TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // решение: время усекаем до микросекунды — это точность timestamptz в Postgres, иначе одна
        // и та же запись отдаёт разное createdAt из памяти и из базы.
        return new DateTime(now.Ticks - now.Ticks % TicksPerMicrosecond, DateTimeKind.Utc);
    }

    public static DateOnly GetUtcToday(this TimeProvider timeProvider)
    {
        return DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
    }
}
