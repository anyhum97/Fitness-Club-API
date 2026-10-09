namespace WebApi.Tests.Infrastructure;

public abstract class ApiTestBase
{
    protected static TestClock Clock => TestApp.Clock;

    protected DateTime Now { get; private set; }

    [TestInitialize]
    public void PinClock()
    {
        Now = Clock.PinToCurrentSecond();
    }

    [TestCleanup]
    public void ReleaseClock()
    {
        Clock.Reset();
    }
}
