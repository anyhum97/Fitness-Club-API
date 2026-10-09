using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace WebApi.Tests.Infrastructure;

public sealed class InMemoryLogSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyList<LogEvent> Events => _events.ToList();

    public void Emit(LogEvent logEvent)
    {
        _events.Enqueue(logEvent);
    }

    public IReadOnlyList<LogEvent> WithTraceId(string traceId)
    {
        return Events
            .Where(x => x.TraceId?.ToHexString() == traceId
                || (x.Properties.TryGetValue("TraceId", out var value) && value.ToString().Trim('"') == traceId))
            .ToList();
    }
}
