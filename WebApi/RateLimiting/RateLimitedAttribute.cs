namespace WebApi.RateLimiting;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RateLimitedAttribute : Attribute
{
}
