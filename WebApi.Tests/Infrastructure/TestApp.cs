using DataAccessLayer;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.Tests.Infrastructure;

public static class TestApp
{
    private static readonly Lazy<ApiApplicationFactory> LazyFactory = new(() =>
    {
        var factory = new ApiApplicationFactory();

        factory.CreateClient().Dispose();

        return factory;
    });

    public static ApiApplicationFactory Factory => LazyFactory.Value;

    public static TestClock Clock => Factory.Clock;

    public static IServiceProvider Services => Factory.Services;

    public static async Task<T> WithDbAsync<T>(Func<ClubDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();

        return await action(scope.ServiceProvider.GetRequiredService<ClubDbContext>());
    }

    public static async Task WithDbAsync(Func<ClubDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();

        await action(scope.ServiceProvider.GetRequiredService<ClubDbContext>());
    }

    public static async Task DisposeAsync()
    {
        if (LazyFactory.IsValueCreated)
        {
            await LazyFactory.Value.DisposeAsync();
        }
    }
}
