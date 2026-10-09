using System.Net;
using System.Text;
using SellDataToAI;
using Xunit;

public class ClientTests
{
    private sealed class Stub : HttpMessageHandler
    {
        private int _polls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            HttpResponseMessage J(HttpStatusCode c, string body) =>
                new(c) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            var key = r.Headers.TryGetValues("X-API-Key", out var v) ? v.First() : "";
            if (key != "k") return Task.FromResult(J(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_api_key\",\"message\":\"bad key\"}"));
            var path = r.RequestUri!.AbsolutePath;
            var q = r.RequestUri.Query;
            if (path == "/v1/score" && q.Contains("domain=bad")) return Task.FromResult(J(HttpStatusCode.BadRequest, "{\"error\":\"invalid_domain\"}"));
            if (path == "/v1/score") return Task.FromResult(J(HttpStatusCode.OK, "{\"domain\":\"example-labs.com\",\"data_asset_score\":82,\"grade\":\"A\",\"history\":{\"pre_ai_years\":25}}"));
            if (path == "/v1/usage") return Task.FromResult(J(HttpStatusCode.OK, "{\"plan\":\"pro\",\"month\":\"2026-10\",\"used\":1,\"limit\":25000,\"remaining\":24999}"));
            if (path == "/v1/score/batch" && r.Method == HttpMethod.Post) return Task.FromResult(J(HttpStatusCode.Accepted, "{\"id\":\"b_1\",\"status\":\"processing\"}"));
            _polls++;
            return Task.FromResult(_polls < 2
                ? J(HttpStatusCode.OK, "{\"id\":\"b_1\",\"status\":\"processing\"}")
                : J(HttpStatusCode.OK, "{\"id\":\"b_1\",\"status\":\"done\",\"results\":[{\"input\":\"a.com\",\"status\":\"done\",\"result\":{\"data_asset_score\":70}}]}"));
        }
    }

    private static DataAssetScoreClient Make(string key = "k") =>
        new(key, "http://stub/v1", new HttpClient(new Stub())) { PollInterval = TimeSpan.FromMilliseconds(10) };

    [Fact]
    public async Task ScoreAndUsage()
    {
        var c = Make();
        var s = await c.ScoreAsync("example-labs.com");
        Assert.Equal("A", s.Grade);
        Assert.Equal(25, s.History!.PreAiYears);
        Assert.Equal(24999, (await c.UsageAsync()).Remaining);
    }

    [Fact]
    public async Task ErrorsCarryStatusAndCode()
    {
        var e = await Assert.ThrowsAsync<SellDataToAIException>(() => Make().ScoreAsync("bad"));
        Assert.Equal(400, e.Status);
        Assert.Equal("invalid_domain", e.Code);
        var e2 = await Assert.ThrowsAsync<SellDataToAIException>(() => Make("x").UsageAsync());
        Assert.Equal(401, e2.Status);
    }

    [Fact]
    public async Task ScoreManyWaits()
    {
        var items = await Make().ScoreManyAsync(new[] { "a.com" });
        Assert.Equal(70, items[0].Result!.DataAssetScore);
    }
}
