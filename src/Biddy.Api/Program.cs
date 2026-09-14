using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHostedService<Processor>();
builder.Services.AddSingleton<AuctionState>();
builder.Services.AddSingleton<Channel<PlaceBidRequest>>(
    _ => Channel.CreateUnbounded<PlaceBidRequest>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        })

/*
_ => Channel.CreateBounded<ChannelRequest>(
new BoundedChannelOptions(1)
{
    FullMode = BoundedChannelFullMode.Wait,
    SingleReader = false,
    AllowSynchronousContinuations = false
})
*/
);

var app = builder.Build();

app.MapGet("/", () => "Hello Auction!");

app.MapPost("bid", async (BidRequest bid, Channel<PlaceBidRequest> channel, CancellationToken ct) =>
{
    var bidId = Guid.CreateVersion7();
    var complete = new TaskCompletionSource<BidResult>(TaskCreationOptions.RunContinuationsAsynchronously);

    try
    {
        await channel.Writer.WriteAsync(
            new PlaceBidRequest(
                bidId,
                bid.AmountInCents,
                bid.UserEmail,
                $"Bid:{bidId} received - {DateTime.UtcNow}",
                complete),
            ct);

        var result = await complete.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        return result.Outcome switch
        {
            BidOutcome.Accepted => Results.Ok(result),
            BidOutcome.Outbid   => Results.Conflict(result),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
    catch (TimeoutException)
    {
        return Results.Accepted(value: new { bidId, status = "pending" });
    }
    catch (ChannelClosedException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (OperationCanceledException) when(!ct.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

public record BidRequest(int AmountInCents, string UserEmail);