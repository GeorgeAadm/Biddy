
using System.Threading.Channels;

public class Processor : BackgroundService
{
    private readonly Channel<ChannelRequest> _channel;

    public Processor(Channel<ChannelRequest> channel)
    {
        _channel = channel;
    }
    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await _channel.Reader.WaitToReadAsync(stoppingToken))
        {
            var request = await _channel.Reader.ReadAsync(stoppingToken);
            await Task.Delay(1000, stoppingToken);
            Console.WriteLine(request.message);
            Console.WriteLine($"Bid:{request.id} processing - {DateTime.UtcNow}");
        }
    }
}

public record ChannelRequest(Guid id, string message);