namespace WebApi.Tests.Infrastructure;

public sealed class TestClock : TimeProvider
{
    private DateTimeOffset? _pinned;

    public DateTime UtcNow => GetUtcNow().UtcDateTime;

    public DateOnly Today => DateOnly.FromDateTime(UtcNow);

    public override DateTimeOffset GetUtcNow()
    {
        return _pinned ?? DateTimeOffset.UtcNow;
    }

    public DateTime PinToCurrentSecond()
    {
        var now = DateTimeOffset.UtcNow;

        _pinned = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

        return _pinned.Value.UtcDateTime;
    }

    public void Advance(TimeSpan delta)
    {
        _pinned = GetUtcNow() + delta;
    }

    public void Reset()
    {
        _pinned = null;
    }
}
