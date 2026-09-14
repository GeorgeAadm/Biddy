using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHostedService<Processor>();
builder.Services.AddSingleton<Channel<ChannelRequest>>(
    _ => Channel.CreateUnbounded<ChannelRequest>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
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

app.MapGet("bid", async (Channel<ChannelRequest> channel) =>
{
    var bidId = Guid.CreateVersion7();
    await channel.Writer.WriteAsync(new ChannelRequest(bidId, $"Bid:{bidId} recieved - {DateTime.UtcNow}"));
    return Results.Ok();
});

app.Run();
