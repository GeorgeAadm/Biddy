using System.Threading.Channels;

public sealed record PlaceBid(
    Guid BidId,
    AuctionState Auction,
    string IdempotencyKey,
    int AmountInCents,
    string UserEmail,
    TaskCompletionSource<BidDecision> Completion)
{
    public static PlaceBid Create(AuctionState auction, string idempotencyKey, int amountInCents, string userEmail) =>
        new(
            Guid.CreateVersion7(),
            auction,
            idempotencyKey,
            amountInCents,
            userEmail,
            new TaskCompletionSource<BidDecision>(TaskCreationOptions.RunContinuationsAsynchronously));
}

public sealed class BidQueue(int capacity)
{
    private readonly Channel<PlaceBid> _channel = Channel.CreateBounded<PlaceBid>(
        new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite -> returns false when full
            SingleReader = true,
            SingleWriter = false,
        });
 
    public bool TryEnqueue(PlaceBid bid) => _channel.Writer.TryWrite(bid);
 
    internal ChannelReader<PlaceBid> Reader => _channel.Reader;
 
    internal void Complete() => _channel.Writer.TryComplete();
}