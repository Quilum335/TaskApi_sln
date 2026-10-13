using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskApi;
using Xunit;

namespace TaskApi.Tests;

public class RabbitMqServiceTest
{
    [Fact]
    public async Task UnavailableBroker_LogsEventsAndStopsWithinFiveSeconds()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var logger = new RecordingLogger();
        using var publisher = new RabbitMqService(Options.Create(new RabbitMqOptions
        {
            HostName = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port
        }), logger);

        await publisher.StartAsync(CancellationToken.None);
        var first = new TaskCompleted(Guid.NewGuid(), "First", DateTimeOffset.UtcNow, Priority.High);
        var second = new TaskCompleted(Guid.NewGuid(), "Second", DateTimeOffset.UtcNow, Priority.Medium);
        publisher.Publish(first);
        publisher.Publish(second);

        var timer = Stopwatch.StartNew();
        await publisher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Contains(logger.Messages, x => x.Contains(first.TaskId.ToString()));
        Assert.Contains(logger.Messages, x => x.Contains(second.TaskId.ToString()));
    }

    private sealed class RecordingLogger : ILogger<RabbitMqService>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}
