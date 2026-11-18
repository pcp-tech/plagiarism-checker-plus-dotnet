using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PlagiarismCheckerPlus
{
    /// <summary>Error codes the API returns in <c>error.code</c>.</summary>
    public static class ErrorCodes
    {
        /// <summary>No longer returned: free accounts get a free API trial and free test keys.</summary>
        [Obsolete("plan_required is no longer returned: free accounts get a free API trial and free test keys.")]
        public const string PlanRequired = "plan_required";
        public const string TextTooShort = "text_too_short";
        public const string DocTooLong = "doc_too_long";
        /// <summary>402: not enough words across all sources (wordsNeeded, wordsAvailable, balances, billingUrl).</summary>
        public const string InsufficientWords = "insufficient_words";
        /// <summary>402: the free API trial cannot cover the request (wordsNeeded, trialRemaining, trialTotal, billingUrl).</summary>
        public const string TrialExhausted = "trial_exhausted";
        /// <summary>402: the key's monthly word cap would be exceeded (wordsNeeded, monthlyWordCap, usedThisMonth).</summary>
        public const string KeyCapReached = "key_cap_reached";
        public const string InvalidRequest = "invalid_request";
        public const string InvalidJson = "invalid_json";
        public const string Unauthorized = "unauthorized";
        public const string InvalidApiKey = "invalid_api_key";
        public const string KeyRevoked = "key_revoked";
        public const string EmailUnverified = "email_unverified";
        public const string Forbidden = "forbidden";
        public const string NotFound = "not_found";
        public const string RateLimited = "rate_limited";
        public const string WorkersUnavailable = "workers_unavailable";
        public const string InternalError = "internal_error";
    }

    /// <summary>
    /// A non-2xx response. <see cref="Code"/> is the API's error code, such as insufficient_words.
    /// <see cref="Details"/> is the response's error object (wordsNeeded, billingUrl and other detail fields).
    /// </summary>
    public class PlagiarismCheckerPlusException : Exception
    {
        public int Status { get; }
        public string? Code { get; }
        public TimeSpan? RetryAfter { get; }
        public JsonNode? Body { get; }

        /// <summary>The response's error object: code, message and any detail fields. Null if the body had none.</summary>
        public JsonObject? Details => Body?["error"] as JsonObject;

        public PlagiarismCheckerPlusException(string message, int status, string? code, TimeSpan? retryAfter, JsonNode? body)
            : base(message)
        {
            Status = status;
            Code = code;
            RetryAfter = retryAfter;
            Body = body;
        }
    }

    /// <summary>Thrown by WaitForScanAsync when the timeout passes.</summary>
    public class ScanTimeoutException : Exception
    {
        public ScanTimeoutException(string message) : base(message) { }
    }

    /// <summary>
    /// Client for the Plagiarism Checker Plus public API. Responses are JsonObject
    /// values that mirror the API's JSON. Get a free API key at
    /// https://app.plagiarismcheckerplus.com/developers (test keys are free and unlimited).
    /// </summary>
    public sealed class PlagiarismCheckerPlusClient
    {
        public const string DefaultBaseUrl = "https://app.plagiarismcheckerplus.com/api/public/v1";
        public const string Version = "1.1.0";

        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly HttpClient _http;

        /// <summary>How WaitForScanAsync sleeps; replaceable in tests.</summary>
        public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (d, ct) => Task.Delay(d, ct);

        public PlagiarismCheckerPlusClient(string apiKey, HttpClient? httpClient = null, string baseUrl = DefaultBaseUrl)
        {
            if (string.IsNullOrEmpty(apiKey)) throw new ArgumentException("apiKey is required", nameof(apiKey));
            _apiKey = apiKey;
            _baseUrl = baseUrl.TrimEnd('/');
            _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>Submit text. tools: any of "plagiarism" and "ai_detect".</summary>
        public Task<JsonObject> CreateScanAsync(string text, IEnumerable<string> tools, string? name = null, CancellationToken ct = default)
        {
            var body = new JsonObject
            {
                ["text"] = text,
                ["tools"] = new JsonArray(tools.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            };
            if (name != null) body["name"] = name;
            return SendAsync(HttpMethod.Post, "/scans", null, body, ct);
        }

        public Task<JsonObject> GetScanAsync(string id, bool includeText = false, CancellationToken ct = default) =>
            SendAsync(HttpMethod.Get, "/scans/" + Uri.EscapeDataString(id), includeText ? "include_text=true" : null, null, ct);

        /// <summary>List scans, newest first. Null page or limit uses the API default.</summary>
        public Task<JsonObject> ListScansAsync(int? page = null, int? limit = null, CancellationToken ct = default)
        {
            var parts = new List<string>();
            if (page.HasValue) parts.Add("page=" + page.Value.ToString(CultureInfo.InvariantCulture));
            if (limit.HasValue) parts.Add("limit=" + limit.Value.ToString(CultureInfo.InvariantCulture));
            return SendAsync(HttpMethod.Get, "/scans", parts.Count > 0 ? string.Join("&", parts) : null, null, ct);
        }

        /// <summary>Plan, word balances, free API trial ("trial"), "balances", "autoTopup" and this key's "key" mode and cap.</summary>
        public Task<JsonObject> GetAccountAsync(CancellationToken ct = default) =>
            SendAsync(HttpMethod.Get, "/account", null, null, ct);

        /// <summary>
        /// Poll until the scan is completed or failed. The delay grows by 1.5x up
        /// to maxInterval, and a 429 waits for Retry-After.
        /// </summary>
        public async Task<JsonObject> WaitForScanAsync(
            string id,
            TimeSpan? timeout = null,
            TimeSpan? interval = null,
            TimeSpan? maxInterval = null,
            bool includeText = false,
            CancellationToken ct = default)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));
            var delay = interval ?? TimeSpan.FromSeconds(2);
            var max = maxInterval ?? TimeSpan.FromSeconds(10);
            while (true)
            {
                var wait = delay;
                try
                {
                    var scan = await GetScanAsync(id, includeText, ct).ConfigureAwait(false);
                    var status = scan["status"]?.GetValue<string>();
                    if (status == "completed" || status == "failed") return scan;
                    delay = TimeSpan.FromTicks(Math.Min((long)(delay.Ticks * 1.5), max.Ticks));
                }
                catch (PlagiarismCheckerPlusException e) when (e.Status == 429)
                {
                    if (e.RetryAfter.HasValue) wait = e.RetryAfter.Value;
                }
                if (DateTime.UtcNow + wait > deadline)
                    throw new ScanTimeoutException($"Scan {id} did not finish within the timeout");
                await Delay(wait, ct).ConfigureAwait(false);
            }
        }

        private async Task<JsonObject> SendAsync(HttpMethod method, string path, string? query, JsonObject? body, CancellationToken ct)
        {
            var url = _baseUrl + path + (query != null ? "?" + query : "");
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("plagiarism-checker-plus-dotnet/" + Version);
            if (body != null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonNode? data = null;
            if (!string.IsNullOrEmpty(raw))
            {
                try { data = JsonNode.Parse(raw); } catch (JsonException) { data = null; }
            }
            var status = (int)response.StatusCode;
            if (status < 200 || status >= 300)
            {
                var error = data?["error"] as JsonObject;
                throw new PlagiarismCheckerPlusException(
                    error?["message"]?.GetValue<string>() ?? "HTTP " + status,
                    status,
                    error?["code"]?.GetValue<string>(),
                    ParseRetryAfter(response.Headers.RetryAfter),
                    data);
            }
            return data as JsonObject ?? new JsonObject();
        }

        private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? value)
        {
            if (value == null) return null;
            if (value.Delta.HasValue) return value.Delta.Value;
            if (value.Date.HasValue)
            {
                var d = value.Date.Value - DateTimeOffset.UtcNow;
                return d > TimeSpan.Zero ? d : TimeSpan.Zero;
            }
            return null;
        }
    }
}
