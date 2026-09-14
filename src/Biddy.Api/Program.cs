using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHostedService<Processor>();
builder.Services.AddSingleton<AuctionState>();
builder.Services.AddSingleton<Channel<ChannelRequest>>(
    _ => Channel.CreateUnbounded<ChannelRequest>(
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

app.MapPost("bid", async (BidRequest bid, Channel<ChannelRequest> channel, CancellationToken ct) =>
{
    var bidId = Guid.CreateVersion7();
    await channel.Writer.WriteAsync(
        new ChannelRequest(
            bidId,
            bid.AmountInCents,
            bid.UserEmail,
            $"Bid:{bidId} recieved - {DateTime.UtcNow}"
        ), ct);

    return Results.Accepted(value: new { bidId });
});

app.Run();

public record BidRequest(int AmountInCents, string UserEmail);