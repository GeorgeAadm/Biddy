
# Biddy
.NET example of auction bids processed through a `Channel`.

Every `POST /bid` is placed on a channel. A single background `Processor` reads bids one at a time, keeps track of the highest bid, and reports the outcome back to the waiting request.

| Response | Meaning |
|---|---|
| `200 OK` | Bid accepted: it's the new highest bid |
| `409 Conflict` | Bid rejected: equal to or lower than the current highest bid |
| `202 Accepted` | Still queued after 10 seconds; it will be processed later |
| `503 Service Unavailable` | The processor is shutting down |

## Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- `curl`, `xargs`, `sort` and `uniq` (included in Git Bash on Windows)

## Run the app
From the repository root:

```bash
dotnet run --project src/Biddy.Api --launch-profile http
```

The API listens on `http://localhost:5138`.

Place a single bid:

```bash
curl -i -H "Content-Type: application/json"
  -d '{"amountInCents":5000,"userEmail":"bidder@test.local"}' \
  http://localhost:5138/bid
```

## Test: 50 bidders send the same amount at once
This checks that bids are processed one at a time. If two bids were ever handled in parallel, both could see "no highest bid yet" and both would get `200`.

With the app running, from Git Bash:

```bash
seq 50 | xargs -P 50 -I{} curl -s -o /dev/null -w "%{http_code}\n" \
  -H "Content-Type: application/json" \
  -d '{"amountInCents":5000,"userEmail":"bidder{}@test.local"}' \
  http://localhost:5138/bid | sort | uniq -c
```

Expected result:

```
      1 200
     49 409
```

The highest bid is kept in memory > so **restart the app before each run** - otherwise the earlier winning bid is still there and all 50 bids get `409`. It should never show more than one `200`.