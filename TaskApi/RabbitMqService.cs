using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace TaskApi;

public interface ITaskEventPublisher
{
    void Publish(TaskCompleted message);
}

public class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
}

public sealed class RabbitMqService(IOptions<RabbitMqOptions> options, ILogger<RabbitMqService> logger)
    : ITaskEventPublisher, IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<Priority>() }
    };

    private readonly Channel<TaskCompleted> _queue = Channel.CreateBounded<TaskCompleted>(new BoundedChannelOptions(1000)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<ITcpClient> _sockets = [];
    private IConnection? _connection;
    private IChannel? _channel;
    private Task _worker = Task.CompletedTask;

    public void Publish(TaskCompleted message)
    {
        if (!_queue.Writer.TryWrite(message))
            logger.LogWarning("Event queue is full or stopped. Unsent event: {Event}", JsonSerializer.Serialize(message, JsonOptions));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker = RunAsync(_stop.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        _stop.CancelAfter(TimeSpan.FromSeconds(4));
        using var registration = cancellationToken.Register(() => _stop.Cancel());
        await _worker;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(ct))
            {
                var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await ConnectAsync(timeout.Token);
                    await _channel!.BasicPublishAsync("task.events", "task.completed", false,
                        new BasicProperties { ContentType = "application/json", Persistent = true }, body, timeout.Token);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not publish event: {Event}", System.Text.Encoding.UTF8.GetString(body));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            while (_queue.Reader.TryRead(out var message))
                logger.LogWarning("Shutdown timeout. Unsent event: {Event}", JsonSerializer.Serialize(message, JsonOptions));
        }
        finally
        {
            await CloseAsync();
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        if (_connection?.IsOpen == true && _channel?.IsOpen == true)
            return;

        await CloseAsync();
        var settings = options.Value;
        var factory = new ConnectionFactory
        {
            HostName = settings.HostName,
            Port = settings.Port,
            UserName = settings.UserName,
            Password = settings.Password,
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(2),
            HandshakeContinuationTimeout = TimeSpan.FromSeconds(2),
            ContinuationTimeout = TimeSpan.FromSeconds(2)
        };
        factory.SocketFactory = addressFamily =>
        {
            var socket = ConnectionFactory.DefaultSocketFactory(addressFamily);
            _sockets.Add(socket);
            if (ct.IsCancellationRequested)
                socket.Dispose();
            return socket;
        };
        // Client cleanup can outlive cancellation without closing the socket.
        using var registration = ct.Register(CloseSockets);
        _connection = await factory.CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(cancellationToken: ct);
        await _channel.ExchangeDeclareAsync("task.events", ExchangeType.Topic, durable: true, cancellationToken: ct);
    }

    private async Task CloseAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var registration = timeout.Token.Register(CloseSockets);
        try
        {
            if (_channel?.IsOpen == true)
                await _channel.CloseAsync(timeout.Token);
            if (_connection?.IsOpen == true)
                await _connection.CloseAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not close RabbitMQ cleanly.");
        }
        finally
        {
            if (_channel is not null)
                await _channel.DisposeAsync();
            if (_connection is not null)
                await _connection.DisposeAsync();
            _channel = null;
            _connection = null;
            while (_sockets.TryTake(out var socket))
                socket.Dispose();
        }
    }

    private void CloseSockets()
    {
        foreach (var socket in _sockets)
            socket.Dispose();
    }

    public void Dispose() => _stop.Dispose();
}
