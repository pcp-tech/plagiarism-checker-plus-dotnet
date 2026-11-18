using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PlagiarismCheckerPlus;
using Xunit;

public class ClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = new();

        public FakeHandler Add(int status, string body, string? retryAfter = null)
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (retryAfter != null) response.Headers.Add("Retry-After", retryAfter);
            _responses.Enqueue(response);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
            Calls.Add((request, body));
            return _responses.Dequeue();
        }
    }

    private static PlagiarismCheckerPlusClient Client(FakeHandler h) =>
        new PlagiarismCheckerPlusClient("pcp_test", new HttpClient(h));

    [Fact]
    public async Task CreateScanSendsKeyAndBody()
    {
        var h = new FakeHandler().Add(202, "{\"id\":\"s1\",\"status\":\"processing\"}");
        var result = await Client(h).CreateScanAsync("text", new[] { "plagiarism" }, "draft");
        Assert.Equal("s1", result["id"]!.GetValue<string>());
        var (request, body) = h.Calls[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://app.plagiarismcheckerplus.com/api/public/v1/scans", request.RequestUri!.ToString());
        Assert.Equal("Bearer pcp_test", request.Headers.Authorization!.ToString());
        Assert.Equal("{\"text\":\"text\",\"tools\":[\"plagiarism\"],\"name\":\"draft\"}", body);
    }

    [Fact]
    public async Task PassesQueryParameters()
    {
        var h = new FakeHandler().Add(200, "{\"id\":\"s1\"}").Add(200, "{\"data\":[]}");
        var c = Client(h);
        await c.GetScanAsync("s1", includeText: true);
        await c.ListScansAsync(2, 5);
        Assert.EndsWith("/scans/s1?include_text=true", h.Calls[0].Request.RequestUri!.ToString());
        Assert.EndsWith("/scans?page=2&limit=5", h.Calls[1].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task ErrorsExposeStatusCodeAndDetails()
    {
        var h = new FakeHandler().Add(402, "{\"error\":{\"code\":\"key_cap_reached\",\"message\":\"Cap reached\",\"monthlyWordCap\":5000,\"usedThisMonth\":4900}}");
        var e = await Assert.ThrowsAsync<PlagiarismCheckerPlusException>(() => Client(h).CreateScanAsync("text", new[] { "plagiarism" }));
        Assert.Equal(402, e.Status);
        Assert.Equal(ErrorCodes.KeyCapReached, e.Code);
        Assert.Equal(5000, e.Details!["monthlyWordCap"]!.GetValue<int>());
        Assert.Equal("trial_exhausted", ErrorCodes.TrialExhausted);
    }

    private static string Sign(string secret, long t, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return string.Concat(hmac.ComputeHash(Encoding.UTF8.GetBytes(t + "." + body)).Select(b => b.ToString("x2")));
    }

    [Fact]
    public void VerifiesWebhookSignatures()
    {
        const string secret = "whsec_test_secret";
        const string body = "{\"id\":\"evt_1\",\"type\":\"scan.completed\",\"data\":{}}";
        const long ts = 1_800_000_000L;
        DateTimeOffset At(long offset) => DateTimeOffset.FromUnixTimeSeconds(ts + offset);
        var header = $"t={ts},v1={Sign(secret, ts, body)}";

        Assert.Equal("PCP-Signature", Webhook.SignatureHeader);
        Assert.True(Webhook.VerifySignature(secret, header, body, now: At(10)));
        Assert.True(Webhook.VerifySignature(secret, header, Encoding.UTF8.GetBytes(body), now: At(0)));
        Assert.True(Webhook.VerifySignature(secret, $"t={ts},v1={Sign("other", ts, body)},v1={Sign(secret, ts, body)}", body, now: At(0)));
        Assert.False(Webhook.VerifySignature("whsec_wrong", header, body, now: At(0)));
        Assert.False(Webhook.VerifySignature(secret, header, body + " ", now: At(0)));
        Assert.False(Webhook.VerifySignature(secret, header, body, now: At(301)));
        Assert.False(Webhook.VerifySignature(secret, header, body, now: At(-301)));
        Assert.True(Webhook.VerifySignature(secret, header, body, TimeSpan.FromSeconds(600), At(500)));
        Assert.False(Webhook.VerifySignature(secret, $"t={ts}", body, now: At(0)));
        Assert.False(Webhook.VerifySignature(secret, null, body, now: At(0)));
    }

    [Fact]
    public async Task WaitHonoursRetryAfter()
    {
        var h = new FakeHandler()
            .Add(200, "{\"id\":\"s1\",\"status\":\"processing\"}")
            .Add(429, "{\"error\":{\"code\":\"rate_limited\",\"message\":\"Slow down\"}}", "3")
            .Add(200, "{\"id\":\"s1\",\"status\":\"completed\"}");
        var waits = new List<TimeSpan>();
        var c = Client(h);
        c.Delay = (d, ct) => { waits.Add(d); return Task.CompletedTask; };
        var scan = await c.WaitForScanAsync("s1", interval: TimeSpan.FromSeconds(1));
        Assert.Equal("completed", scan["status"]!.GetValue<string>());
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) }, waits.ToArray());
    }

    [Fact]
    public async Task WaitTimesOut()
    {
        var h = new FakeHandler().Add(200, "{\"id\":\"s1\",\"status\":\"processing\"}");
        var c = Client(h);
        c.Delay = (d, ct) => Task.CompletedTask;
        await Assert.ThrowsAsync<ScanTimeoutException>(() =>
            c.WaitForScanAsync("s1", timeout: TimeSpan.FromMilliseconds(10), interval: TimeSpan.FromSeconds(1)));
    }
}
