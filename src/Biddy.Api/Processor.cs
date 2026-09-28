
// The only code that changes auction state.
// One reader means bids are applied strictly one at a time, in arrival order, without locks.
// This loop is also where persistence would go.

public sealed class BidProcessor(
    BidQueue queue,
    TimeProvider time,
    ILogger<BidProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var bid in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    bid.Completion.TrySetResult(Process(bid));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Bid {BidId} on auction {AuctionId} failed", bid.BidId, bid.Auction.Id);
                    bid.Completion.TrySetException(ex);
                }
            }
        }
        finally
        {
            // Stop accepting new bids -> then release still waiting.
            queue.Complete();
            while (queue.Reader.TryRead(out var pending)) 
                pending.Completion.TrySetCanceled();
        }
    }
    
    private BidDecision Process(PlaceBid bid)
    {
        var auction = bid.Auction;

        if (auction.TryGetProcessed(bid.IdempotencyKey, out var previous))
        {
            if (!previous.Matches(bid))
            {
                logger.LogWarning("Idempotency key reused with a different bid on auction {AuctionId}", auction.Id);
                return new IdempotencyKeyConflict();
            }

            logger.LogInformation("Bid {BidId} replayed on auction {AuctionId}", previous.Result.BidId, auction.Id);
            return new BidProcessed(previous.Result, Replayed: true);
        }

        var result = Decide(bid);
        auction.RecordProcessed(bid.IdempotencyKey, new ProcessedBid(bid.AmountInCents, bid.UserEmail, result));
        return new BidProcessed(result, Replayed: false);
    }

    private BidResult Decide(PlaceBid bid)
    {
        var auction = bid.Auction;
        var current = auction.Highest;
        var now = time.GetUtcNow().UtcDateTime;
 
        if (current is not null && bid.AmountInCents <= current.AmountInCents)
        {
            logger.LogInformation("Bid {BidId} on auction {AuctionId} outbid: {Amount} <= {Highest}",
                bid.BidId, auction.Id, bid.AmountInCents, current.AmountInCents);
 
            return new BidResult(bid.BidId, auction.Id, BidOutcome.Outbid, current.AmountInCents, now);
        }
        
        auction.SetHighest(new WinningBid(bid.BidId, bid.AmountInCents, bid.UserEmail, now));

        logger.LogInformation("Bid {BidId} on auction {AuctionId} accepted: {Amount}",
            bid.BidId, auction.Id, bid.AmountInCents);

        return new BidResult(bid.BidId, auction.Id, BidOutcome.Accepted, bid.AmountInCents, now);
    }
}

