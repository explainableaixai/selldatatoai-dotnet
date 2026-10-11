# AlphaQuantum.SellDataToAI

.NET client for the AI Data Value Score API. It scores a company website from 0 to 100 for the data AI buyers want, and tells you the company's grade, status, systems of record and web history.

```bash
dotnet add package AlphaQuantum.SellDataToAI
```

```csharp
using SellDataToAI;
```

Targets .NET 8. Uses `HttpClient` and `System.Text.Json` from the framework, no other packages. Async all the way, with `CancellationToken` on every call.

The API is part of selldatatoai.com, a site about [selling data to AI companies](https://www.selldatatoai.com/). It scores against an index of 120 million domains, 99.99% of the active internet, with each domain's history.

## Quick start

```csharp
using SellDataToAI;

using var client = new DataAssetScoreClient(Environment.GetEnvironmentVariable("SDA_API_KEY")!);

var s = await client.ScoreAsync("example.com");
Console.WriteLine($"{s.Domain}: {s.DataAssetScore} ({s.Grade}), {s.Status}");
Console.WriteLine($"Online {s.History?.YearsOnline} years, {s.History?.PreAiYears} before 2023");

foreach (var sys in s.DataSystems)
    Console.WriteLine($"  {sys.System}: {sys.DataType}");

var u = await client.UsageAsync();
Console.WriteLine($"{u.Remaining} of {u.Limit} lookups left in {u.Month}");
```

Full URLs work as input. The API takes the domain from them.

## Types

`ScoreResult` maps the JSON answer to properties:

| Property | Type | JSON field |
|---|---|---|
| `Domain` | `string` | `domain` |
| `DataAssetScore` | `int` | `data_asset_score` |
| `Grade` | `string` | `grade` (A to E) |
| `Status` | `string` | `status` |
| `VerifiedActive` | `bool` | `verified_active` |
| `IabCategory` | `string?` | `iab_category` |
| `Country` | `string?` | `country` |
| `History` | `History?` | `history` |
| `PreAiArchiveLikely` | `bool` | `pre_ai_archive_likely` |
| `DataSystems` | `List<DataSystem>` | `data_systems` |
| `LikelyDataAssets` | `List<DataAsset>` | `likely_data_assets` |
| `Strengths` | `List<string>` | `strengths` |
| `DataCoverage` | `string?` | `data_coverage` |
| `InIndex` | `bool` | `in_index` |
| `CheckedAt` | `string?` | `checked_at` |
| `Cached` | `bool` | `cached` |

`History` is a record with `FirstSeenYear`, `YearsOnline`, `FoundedYear` and `PreAiYears`, all `int?`. `DataSystem` has `System`, `DataType` and `Type`. `DataAsset` has `Type` and `Label`.

`Batch`, `BatchItem` and `Usage` mirror the batch and usage answers. All field meanings are in the [API documentation](https://www.selldatatoai.com/api/).

## Methods

| Method | Endpoint | Lookups |
|---|---|---|
| `ScoreAsync(domain, ct)` | `GET /api/v1/score` | 1 |
| `SubmitBatchAsync(domains, ct)` | `POST /api/v1/score/batch` | 1 per valid, unique domain |
| `GetBatchAsync(id, ct)` | `GET /api/v1/score/batch?id=` | 0 |
| `WaitForBatchAsync(id, ct)` | polls until done | 0 |
| `ScoreManyAsync(domains, ct)` | batches of 100, input order | 1 per valid, unique domain |
| `UsageAsync(ct)` | `GET /api/v1/usage` | 0 |

Properties `PollInterval` (5 seconds) and `MaxWait` (5 minutes) control batch polling.

## Using it with dependency injection

In ASP.NET Core, let the host manage the `HttpClient`:

```csharp
// Program.cs
builder.Services.AddHttpClient("sda", c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("sda");
    var key = builder.Configuration["SellDataToAI:ApiKey"]
              ?? throw new InvalidOperationException("SellDataToAI:ApiKey is not set");
    return new DataAssetScoreClient(key, httpClient: http);
});
```

When you pass an `HttpClient`, the client does not dispose it. When you do not, it creates one with a 60 second timeout and disposes it in `Dispose()`.

Keep the key in user secrets locally and in your secret store in production:

```bash
dotnet user-secrets set "SellDataToAI:ApiKey" "xxxx"
```

## Errors

Non-2xx answers throw `SellDataToAIException` with `Status`, `Code` and `Body`.

```csharp
try
{
    var s = await client.ScoreAsync(input, ct);
    Save(s);
}
catch (SellDataToAIException e) when (e.Code == "invalid_domain")
{
    logger.LogInformation("Skipping {Input}", input);
}
catch (SellDataToAIException e) when (e.Code == "monthly_limit_reached")
{
    throw new ApplicationException("Lookups used up for this month", e);
}
catch (SellDataToAIException e) when (e.Status == 401)
{
    throw new ApplicationException("Check the API key and that the plan is active", e);
}
```

| HTTP | Code | Meaning |
|---|---|---|
| 400 | `invalid_domain` | The domain is not valid |
| 400 | `no_domains`, `too_many_domains` | A batch needs 1 to 100 domains |
| 401 | `missing_api_key`, `invalid_api_key` | No key, or the key is not active |
| 404 | `batch_not_found` | Unknown id for this key, or older than 7 days |
| 410 | `not_available` | Company lists are not served by the API |
| 429 | `monthly_limit_reached` | Monthly lookups used, reset on the 1st (UTC) |
| 429 | `batch_busy` | Three of your batches are still running |

`ArgumentException` is thrown before any request for an empty key, an empty domain or a batch outside 1 to 100. `TaskCanceledException` signals a timeout or a cancelled token.

## Examples

### Minimal API endpoint

```csharp
app.MapPost("/partners", async (PartnerForm form, DataAssetScoreClient sda, CancellationToken ct) =>
{
    try
    {
        var s = await sda.ScoreAsync(form.Website, ct);
        var tier = s.VerifiedActive && (s.Grade == "A" || s.Grade == "B") ? "priority" : "standard";
        return Results.Ok(new { form.Company, tier, s.DataAssetScore, s.Grade });
    }
    catch (SellDataToAIException e) when (e.Status == 400)
    {
        return Results.UnprocessableEntity(new { error = "Website is not valid" });
    }
});

record PartnerForm(string Company, string Website);
```

### Background service that scores new accounts every night

```csharp
public sealed class NightlyScorer(DataAssetScoreClient sda, IAccountStore store, ILogger<NightlyScorer> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var domains = await store.UnscoredDomainsAsync(ct);
            if (domains.Count > 0)
            {
                var left = (await sda.UsageAsync(ct)).Remaining;
                var take = domains.Take((int)Math.Min(left, domains.Count)).ToList();
                var items = await sda.ScoreManyAsync(take, ct);
                foreach (var it in items.Where(i => i.Result is not null))
                    await store.SaveScoreAsync(it.Input, it.Result!, ct);
                log.LogInformation("Scored {Count} accounts, {Left} lookups left", items.Count, left - take.Count);
            }
            await Task.Delay(TimeSpan.FromHours(24), ct);
        }
    }
}
```

The service never sends more domains than the plan has lookups left, so a large import cannot run into the monthly limit halfway.

### Console ranker

```csharp
var key = Environment.GetEnvironmentVariable("SDA_API_KEY")!;
using var sda = new DataAssetScoreClient(key);

var domains = File.ReadAllLines(args[0]).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
var items = await sda.ScoreManyAsync(domains);

foreach (var s in items.Select(i => i.Result).OfType<ScoreResult>()
                       .Where(s => s.VerifiedActive)
                       .OrderByDescending(s => s.DataAssetScore)
                       .Take(25))
{
    Console.WriteLine($"{s.Domain,-32} {s.DataAssetScore,3} {s.Grade}  {string.Join(", ", s.DataSystems.Select(d => d.System))}");
}
```

## Reading a result

Sort by score only after two checks:

1. `VerifiedActive` must be `true`. Otherwise the domain is expired, parked or shows an error page.
2. `Status` should be `active`. A `winding_down_or_acquired` company may still hold records, but the person who can sign has changed.

Then use the rest:

- `Grade` A or B is where buyers usually start.
- `DataSystems` lists systems the company runs. A running ticketing or CRM system means records exist and can be exported.
- `PreAiArchiveLikely` marks companies with 10 or more years online before 2023. Archives from that period contain no AI-written text.
- `DataCoverage` of `limited` means fewer signals were available. Weigh the score with more caution.

## Batch behaviour

- 1 to 100 domains per call. Invalid rows come back as `invalid_domain` and are free.
- One lookup per valid, unique domain, charged when you submit.
- Domains scored in the last 30 days are `done` in the first answer. The rest usually finish within a minute.
- Polling is free. Results keep input order and are kept for 7 days.
- Up to three open batches per key. `ScoreManyAsync` sends one at a time, so it stays within the limit.

## What people use the score for

Brokers, data companies and referral partners place company records with AI labs. The hard part is finding sellers: companies with years of records, in systems that export, that are still in business. The score ranks candidates on exactly that.

Two questions come up in almost every first conversation with a seller. The first is legal, and the guide [is it legal to sell company data](https://www.selldatatoai.com/is-it-legal-to-sell-company-data/) walks through what a seller has to check before signing. The second is who pays for an introduction, which the overview of [data referral programs](https://www.selldatatoai.com/data-referral-programs/) covers from the programs' own published terms.

Life sciences is a common starting sector because studies produce structured records over many years. The [list of contract research organizations](https://www.selldatatoai.com/list-of-contract-research-organizations/) page scores US CROs as data partners. Clinical care is similar, and a sector file of US healthcare companies is described on the [healthcare provider list](https://www.selldatatoai.com/lists/healthcare-providers/) page.

When a buyer's profile does not match a ready sector, the site also builds [custom company lists](https://www.selldatatoai.com/custom-lists/) to a stated ICP. Lists of any kind are separate from the API and never returned by it.

## Plans and pricing

| Plan | Price | Lookups a month | Batch scoring |
|---|---|---|---|
| Basic | $99 a month | 5,000 | no |
| Pro | $299 a month | 25,000 | yes |
| Scale | $799 a month | 100,000 | yes |

There is no free plan and no trial. The demo on the website allows 5 checks a day. The key appears in your dashboard as soon as payment completes, and limits reset on the first day of each month, UTC.

## FAQ

### What is AlphaQuantum.SellDataToAI?

AlphaQuantum.SellDataToAI is the .NET client for the AI Data Value Score API at selldatatoai.com. It scores company domains from 0 to 100 for the data AI buyers want, singly or in batches of 100, and returns typed results.

### Which frameworks are supported?

.NET 8 and newer. The package uses only framework libraries.

### Is the client thread-safe?

Yes. Register it as a singleton and share it. Configure `PollInterval` and `MaxWait` once at startup.

### What does a cached answer cost?

The same as a fresh one: one lookup. `Cached` is `true` when the result was taken in the last 30 days.

### How do I test my code without using lookups?

Pass an `HttpClient` built on your own `HttpMessageHandler` that returns the documented JSON. The tests in the repository use that pattern.

### Are the weights behind the score published?

No. The factor groups are explained on the website; the weights and rules are private.

## Links

- Package: [nuget.org](https://www.nuget.org/packages/AlphaQuantum.SellDataToAI)
- `IHttpClientFactory` guidance: [learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-factory)
- Website: https://www.selldatatoai.com/
- Source: https://github.com/explainableaixai/selldatatoai-dotnet

## License

MIT. Copyright (c) 2026 Alpha Quantum, info@alpha-quantum.com
