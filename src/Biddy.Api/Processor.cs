
using System.Threading.Channels;

public sealed class AuctionState
{
    private WinningBid? _highest;
    public WinningBid? Highest => Volatile.Read(ref _highest);
    public void SetHighest(WinningBid bid) => Volatile.Write(ref _highest, bid);
}
public sealed class Processor : BackgroundService
{
    private readonly Channel<ChannelRequest> _channel;
    private readonly AuctionState _state;

    public Processor(Channel<ChannelRequest> channel, AuctionState state)
    {
        _channel = channel;
        _state = state;
    }
    protected async override Task ExecuteAsync(CancellationToken ct)
    {
        await foreach(var request in _channel.Reader.ReadAllAsync(ct))
        {
            var current = _state.Highest;
            await Task.Delay(1000, ct);
            Console.WriteLine(request.message);

            if(current is null || request.amountInCents > current.AmountInCents)
            {
                _state.SetHighest(new WinningBid(request.id, request.amountInCents, request.userEmail, DateTime.UtcNow));
                Console.WriteLine($"Bid:{request.id} accepted - {DateTime.UtcNow}");
            }
            else
            {
                Console.WriteLine($"Bid:{request.id} rejected - {DateTime.UtcNow}");
            }
        }
    }
}

public sealed record ChannelRequest(Guid id, int amountInCents, string userEmail, string message);
public sealed record WinningBid(Guid Id, int AmountInCents, string UserEmail, DateTime AcceptedAtUtc);
