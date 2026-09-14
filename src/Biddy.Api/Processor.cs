
using System.Threading.Channels;

public sealed class AuctionState
{
    private WinningBid? _highest;
    public WinningBid? Highest => Volatile.Read(ref _highest);
    public void SetHighest(WinningBid bid) => Volatile.Write(ref _highest, bid);
}
public sealed class Processor : BackgroundService
{
    private readonly Channel<PlaceBidRequest> _channel;
    private readonly AuctionState _state;

    public Processor(Channel<PlaceBidRequest> channel, AuctionState state)
    {
        _channel = channel;
        _state = state;
    }
    
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    // await Task.Delay(1000, ct);
                    Console.WriteLine(request.message);

                    var current = _state.Highest;
                    var now = DateTime.UtcNow;

                    if (current is null || request.amountInCents > current.AmountInCents)
                    {
                        _state.SetHighest(new WinningBid(request.id, request.amountInCents, request.userEmail, now));
                        Console.WriteLine($"Bid:{request.id} ACCEPTED A - {now}");
                        request.complete.TrySetResult(
                            new BidResult(request.id, BidOutcome.Accepted, request.amountInCents, now));
                    }
                    else
                    {
                        Console.WriteLine($"Bid:{request.id} REJECTED R - {now}");
                        request.complete.TrySetResult(
                            new BidResult(request.id, BidOutcome.Outbid, current.AmountInCents, now));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    request.complete.TrySetCanceled(ct);
                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Bid:{request.id} failed - {ex.Message}");
                    request.complete.TrySetException(ex);
                }
            }
        }
        finally
        {
            _channel.Writer.TryComplete();
            while (_channel.Reader.TryRead(out var pending))
                pending.complete.TrySetCanceled();
        }
    }
}

public enum BidOutcome
{
    Accepted,
    Outbid
}
public sealed record PlaceBidRequest(
    Guid id,
    int amountInCents,
    string userEmail,
    string message,
    TaskCompletionSource<BidResult> complete);

public sealed record WinningBid(
    Guid Id, 
    int AmountInCents, 
    string UserEmail, 
    DateTime AcceptedAtUtc);

public sealed record BidResult(
    Guid BidId,
    BidOutcome Outcome,
    int HighestAmountInCents,
    DateTime ProcessedAtUtc);