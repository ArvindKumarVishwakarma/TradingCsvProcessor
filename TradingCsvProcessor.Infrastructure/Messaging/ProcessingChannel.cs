using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TradingCsvProcessor.Application.Interfaces;
using TradingCsvProcessor.Application.Options;

namespace TradingCsvProcessor.Infrastructure.Messaging;

// In-process bounded channel for job queuing.
// For cross-process durability, replace with a DB-backed queue or message broker (RabbitMQ / Azure Service Bus).
public sealed class ProcessingChannel : IJobQueue
{
    private readonly Channel<Guid> _channel;

    public ProcessingChannel(IOptions<ProcessingOptions> options)
    {
        _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(options.Value.ChannelCapacity)
        {
            FullMode    = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ChannelWriter<Guid> Writer => _channel.Writer;
    public ChannelReader<Guid> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(Guid jobId, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(jobId, ct);
}
