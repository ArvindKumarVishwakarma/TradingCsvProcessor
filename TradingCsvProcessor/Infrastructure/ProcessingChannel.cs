using System.Threading.Channels;

namespace TradingCsvProcessor.Infrastructure;

// In-process bounded channel for job queuing.
// For cross-process durability, replace with a DB-backed queue or message broker (RabbitMQ, Azure Service Bus).
public sealed class ProcessingChannel
{
    private readonly Channel<Guid> _channel;

    public ProcessingChannel(IConfiguration config)
    {
        var capacity = config.GetValue<int>("Processing:ChannelCapacity", 1000);
        _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public ChannelWriter<Guid> Writer => _channel.Writer;
    public ChannelReader<Guid> Reader => _channel.Reader;

    public async ValueTask EnqueueAsync(Guid jobId, CancellationToken ct = default)
    {
        await _channel.Writer.WriteAsync(jobId, ct);
    }
}
