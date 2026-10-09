using WebApi.Infrastructure;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Units;

[TestClass]
public class TimeProviderExtensionsTests
{
    [TestMethod]
    public void GetUtcNowTruncated_DropsSubMicrosecondTicks()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567));

        var now = clock.GetUtcNowTruncated();

        Assert.AreEqual(DateTimeKind.Utc, now.Kind);
        Assert.AreEqual(0, now.Ticks % 10);
        Assert.AreEqual(new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560), now);
    }

    [TestMethod]
    public void GetUtcToday_UsesUtcDate()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 9, 23, 30, 0, TimeSpan.FromHours(-5)));

        Assert.AreEqual(new DateOnly(2026, 10, 10), clock.GetUtcToday());
    }

    [TestMethod]
    public void TestClock_AdvancesFromPinnedMoment()
    {
        var clock = new TestClock();
        var pinned = clock.PinToCurrentSecond();

        clock.Advance(TimeSpan.FromHours(2));

        Assert.AreEqual(pinned.AddHours(2), clock.UtcNow);
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }
    }
}
