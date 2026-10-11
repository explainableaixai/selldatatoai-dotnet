using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SellDataToAI;

/// <summary>Web history of a company domain.</summary>
public sealed record History(
    [property: JsonPropertyName("first_seen_year")] int? FirstSeenYear,
    [property: JsonPropertyName("years_online")] int? YearsOnline,
    [property: JsonPropertyName("founded_year")] int? FoundedYear,
    [property: JsonPropertyName("pre_ai_years")] int? PreAiYears);

/// <summary>A system of record the company runs, with the data type it holds.</summary>
public sealed record DataSystem(
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("data_type")] string DataType,
    [property: JsonPropertyName("type")] string Type);

/// <summary>A data type the company likely holds.</summary>
public sealed record DataAsset(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("label")] string Label);

/// <summary>One scored company.</summary>
public sealed class ScoreResult
{
    [JsonPropertyName("domain")] public string Domain { get; init; } = "";
    [JsonPropertyName("data_asset_score")] public int DataAssetScore { get; init; }
    [JsonPropertyName("grade")] public string Grade { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("verified_active")] public bool VerifiedActive { get; init; }
    [JsonPropertyName("iab_category")] public string? IabCategory { get; init; }
    [JsonPropertyName("country")] public string? Country { get; init; }
    [JsonPropertyName("history")] public History? History { get; init; }
    [JsonPropertyName("pre_ai_archive_likely")] public bool PreAiArchiveLikely { get; init; }
    [JsonPropertyName("data_systems")] public List<DataSystem> DataSystems { get; init; } = new();
    [JsonPropertyName("likely_data_assets")] public List<DataAsset> LikelyDataAssets { get; init; } = new();
    [JsonPropertyName("strengths")] public List<string> Strengths { get; init; } = new();
    [JsonPropertyName("data_coverage")] public string? DataCoverage { get; init; }
    [JsonPropertyName("in_index")] public bool InIndex { get; init; }
    [JsonPropertyName("checked_at")] public string? CheckedAt { get; init; }
    [JsonPropertyName("cached")] public bool Cached { get; init; }
}

/// <summary>One row of a batch, in the order it was sent.</summary>
public sealed class BatchItem
{
    [JsonPropertyName("input")] public string Input { get; init; } = "";
    [JsonPropertyName("domain")] public string? Domain { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("result")] public ScoreResult? Result { get; init; }
}

/// <summary>Answer to a batch submit or poll.</summary>
public sealed class Batch
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("total")] public int Total { get; init; }
    [JsonPropertyName("pending")] public int Pending { get; init; }
    [JsonPropertyName("results")] public List<BatchItem> Results { get; init; } = new();
    [JsonPropertyName("lookups_charged")] public int? LookupsCharged { get; init; }
}

/// <summary>This month's lookups.</summary>
public sealed record Usage(
    [property: JsonPropertyName("plan")] string Plan,
    [property: JsonPropertyName("month")] string Month,
    [property: JsonPropertyName("used")] long Used,
    [property: JsonPropertyName("limit")] long Limit,
    [property: JsonPropertyName("remaining")] long Remaining);

/// <summary>Thrown for a non-2xx answer. <see cref="Code"/> is the API error string.</summary>
public sealed class SellDataToAIException : Exception
{
    public int Status { get; }
    public string? Code { get; }
    public string? Body { get; }
    public SellDataToAIException(int status, string? code, string message, string? body = null) : base(message)
    {
        Status = status; Code = code; Body = body;
    }
}

/// <summary>Client for the AI Data Value Score API at https://www.selldatatoai.com/api/ .</summary>
public sealed class DataAssetScoreClient : IDisposable
{
    public const string Version = "1.0.1";
    public const string DefaultBaseUrl = "https://www.selldatatoai.com/api/v1";
    public const int BatchMax = 100;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _base;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromMinutes(5);

    public DataAssetScoreClient(string apiKey, string? baseUrl = null, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("An API key is required. Plans: https://www.selldatatoai.com/pricing/", nameof(apiKey));
        _base = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Remove("X-API-Key");
        _http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"selldatatoai-dotnet/{Version} (+https://www.selldatatoai.com)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            string? code = null, msg = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("error", out var e)) code = e.GetString();
                if (doc.RootElement.TryGetProperty("message", out var m)) msg = m.GetString();
            }
            catch (JsonException) { }
            throw new SellDataToAIException((int)res.StatusCode, code, msg ?? $"HTTP {(int)res.StatusCode}", text);
        }
        return JsonSerializer.Deserialize<T>(text)
            ?? throw new SellDataToAIException((int)res.StatusCode, "empty_body", "The API returned an empty body", text);
    }

    /// <summary>Score one company domain. Full URLs are accepted.</summary>
    public Task<ScoreResult> ScoreAsync(string domain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domain)) throw new ArgumentException("domain is empty", nameof(domain));
        var url = $"{_base}/score?domain={Uri.EscapeDataString(domain.Trim())}";
        return SendAsync<ScoreResult>(new HttpRequestMessage(HttpMethod.Get, url), ct);
    }

    /// <summary>Submit 1 to 100 domains. One lookup per valid, unique domain.</summary>
    public Task<Batch> SubmitBatchAsync(IReadOnlyList<string> domains, CancellationToken ct = default)
    {
        if (domains.Count == 0 || domains.Count > BatchMax)
            throw new ArgumentException($"A batch takes 1 to {BatchMax} domains", nameof(domains));
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/score/batch")
        {
            Content = JsonContent.Create(new { domains })
        };
        return SendAsync<Batch>(req, ct);
    }

    /// <summary>Poll a batch. Polling is free.</summary>
    public Task<Batch> GetBatchAsync(string id, CancellationToken ct = default) =>
        SendAsync<Batch>(new HttpRequestMessage(HttpMethod.Get, $"{_base}/score/batch?id={Uri.EscapeDataString(id)}"), ct);

    /// <summary>Poll until the batch is done or <see cref="MaxWait"/> passes.</summary>
    public async Task<Batch> WaitForBatchAsync(string id, CancellationToken ct = default)
    {
        var stop = DateTime.UtcNow + MaxWait;
        while (true)
        {
            var b = await GetBatchAsync(id, ct).ConfigureAwait(false);
            if (b.Status == "done") return b;
            if (DateTime.UtcNow > stop) throw new SellDataToAIException(0, "batch_timeout", $"Batch {id} still processing");
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Score any number of domains in batches of 100; items come back in input order.</summary>
    public async Task<List<BatchItem>> ScoreManyAsync(IEnumerable<string> domains, CancellationToken ct = default)
    {
        var all = domains.ToList();
        var output = new List<BatchItem>(all.Count);
        foreach (var chunk in all.Chunk(BatchMax))
        {
            var b = await SubmitBatchAsync(chunk, ct).ConfigureAwait(false);
            if (b.Status != "done") b = await WaitForBatchAsync(b.Id, ct).ConfigureAwait(false);
            output.AddRange(b.Results);
        }
        return output;
    }

    /// <summary>Plan, lookups used this month and lookups remaining.</summary>
    public Task<Usage> UsageAsync(CancellationToken ct = default) =>
        SendAsync<Usage>(new HttpRequestMessage(HttpMethod.Get, $"{_base}/usage"), ct);

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
