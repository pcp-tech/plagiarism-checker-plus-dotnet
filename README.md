# Plagiarism Checker Plus for .NET

Official client for the [Plagiarism Checker Plus API](https://plagiarismcheckerplus.com/integrations/api). Targets .NET 8 and .NET Standard 2.0. Uses `HttpClient` and `System.Text.Json`.

Get a free API key in the [developer portal](https://app.plagiarismcheckerplus.com/developers). Test keys are free and unlimited, and every free account gets 2,000 live API words once, with no card. See [API pricing](https://plagiarismcheckerplus.com/api-pricing).

## Install

```sh
dotnet add package PlagiarismCheckerPlus
```

## Quick start

Get a free API key at https://app.plagiarismcheckerplus.com/developers (test keys are free and unlimited).

```csharp
using PlagiarismCheckerPlus;

var client = new PlagiarismCheckerPlusClient(Environment.GetEnvironmentVariable("PCP_API_KEY")!);

try
{
    // 50+ words for AI detection.
    var created = await client.CreateScanAsync(essayText, new[] { "plagiarism", "ai_detect" }, "essay-draft-1");
    var scan = await client.WaitForScanAsync(created["id"]!.GetValue<string>());

    if (scan["sandbox"]?.GetValue<bool>() == true) Console.WriteLine("Test key: these are sample results.");
    var plagiarism = scan["results"]?["plagiarism"];
    Console.WriteLine($"Matched: {plagiarism?["score"]}%");
    foreach (var source in plagiarism?["sources"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
        Console.WriteLine(source?["url"]);

    // A likelihood, not proof that the text was written by AI.
    Console.WriteLine($"AI likelihood: {scan["results"]?["aiDetection"]?["score"]}%");
}
catch (PlagiarismCheckerPlusException e) when (e.Code == ErrorCodes.TextTooShort)
{
    Console.WriteLine("Send at least 30 words for plagiarism, 50 for AI detection.");
}
catch (PlagiarismCheckerPlusException e) when (e.Code == ErrorCodes.TrialExhausted)
{
    Console.WriteLine($"Free API trial used up. Buy words or choose an API plan: {e.Details?["billingUrl"]}");
}
catch (PlagiarismCheckerPlusException e) when (e.Code == ErrorCodes.InsufficientWords)
{
    Console.WriteLine($"Need {e.Details?["wordsNeeded"]} words, {e.Details?["wordsAvailable"]} available. Top up: {e.Details?["billingUrl"]}");
}
catch (PlagiarismCheckerPlusException e) when (e.Code == ErrorCodes.KeyCapReached)
{
    Console.WriteLine($"This key reached its monthly cap of {e.Details?["monthlyWordCap"]} words.");
}
```

Responses are `JsonObject` values that mirror the API's JSON, so new fields (such as `sandbox` on scans and `trial`, `balances`, `autoTopup` and `key` on the account) reach you without an SDK update.

## Test keys, free trial and pricing

- **Test keys** (`pcp_test_...`) are free and unlimited on every plan, including free. They never run a check: `CreateScanAsync` returns as usual and `GetScanAsync` returns `completed` with fixed sample results in the same shape as a real scan, marked `"sandbox": true`. Use them to build and test your integration.
- **Free API trial**: each free account gets 2,000 API words once for live keys (`pcp_...`), with no card. When the trial cannot cover a request, the API returns `402 trial_exhausted` and nothing is charged.
- **Paid app plans** (Student, Personal, Team) use their plan words for API calls.
- **API plans**: Starter $25/month (300,000 words), Growth $99/month (1,500,000 words), Scale $399/month (7,500,000 words).
- **Pay as you go**: words from $4.50 (50,000 words, $0.09 per 1,000 words). Purchased words never expire.

A scan costs its word count once per tool: 800 words checked for plagiarism and AI costs 1,600 words. Prices: [API pricing](https://plagiarismcheckerplus.com/api-pricing). Keys, usage, billing and webhooks: [developer portal](https://app.plagiarismcheckerplus.com/developers). `GetAccountAsync()` returns what is left in `trial` and `balances`, plus `autoTopup` and this key's `key.mode` and `key.monthlyWordCap`.

The rate limit is 60 requests a minute per key on paid app plans and for accounts with an API plan or purchased API words, and 10 a minute otherwise (free trial, test keys on the free plan).

## Webhooks

Instead of polling, register an https endpoint in the [developer portal](https://app.plagiarismcheckerplus.com/developers) to receive `scan.completed` and `scan.failed` for checks made with live keys. Each delivery carries

```
PCP-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256(secret, "<t>.<raw body>")>
```

`Webhook.VerifySignature(secret, header, rawBody, tolerance)` checks it with the endpoint's signing secret (`whsec_...`). Pass the raw request body, before JSON parsing. It compares in constant time and rejects timestamps more than 300 seconds from now (the tolerance is configurable). It accepts the body as a string or as bytes.

```csharp
using PlagiarismCheckerPlus;

app.MapPost("/webhooks/pcp", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var raw = await reader.ReadToEndAsync(); // raw body, before JSON parsing
    var header = request.Headers[Webhook.SignatureHeader].ToString();
    if (!Webhook.VerifySignature(Environment.GetEnvironmentVariable("PCP_WEBHOOK_SECRET")!, header, raw))
        return Results.BadRequest("Invalid signature");

    var evt = System.Text.Json.Nodes.JsonNode.Parse(raw)!;
    var type = evt["type"]!.GetValue<string>();
    if (type == "scan.completed") Console.WriteLine($"Scan done: {evt["data"]?["scan"]?["id"]}");
    else if (type == "scan.failed") Console.WriteLine($"Scan failed: {evt["data"]?["scan"]?["id"]}");
    return Results.Ok();
});
```

An event can be delivered more than once (retries). Use its `id` to ignore repeats.

## Methods

| Method | API call |
|---|---|
| `CreateScanAsync(text, tools, name)` | `POST /scans` |
| `GetScanAsync(id, includeText)` | `GET /scans/{id}` |
| `ListScansAsync(page, limit)` | `GET /scans` |
| `GetAccountAsync()` | `GET /account` |
| `WaitForScanAsync(id, timeout, interval, maxInterval)` | Polls until `completed` or `failed` |
| `Webhook.VerifySignature(secret, header, body, tolerance, now)` | Checks a webhook's `PCP-Signature` (no API call) |

`WaitForScanAsync` starts at 2 seconds, grows by 1.5x up to 10 seconds, waits for `Retry-After` on `429`, and throws `ScanTimeoutException` after 5 minutes by default.

Failed calls throw `PlagiarismCheckerPlusException` with `Status`, `Code` (such as `trial_exhausted`, `insufficient_words`, `text_too_short`), `RetryAfter`, `Body` and `Details` (the response's error object).

Run the tests with `dotnet test tests/PlagiarismCheckerPlus.Tests`.

## Error codes

Failed calls raise `PlagiarismCheckerPlusException`. It carries the HTTP status and the API error code (constants in `ErrorCodes`):

| Code | Status | Meaning |
|---|---|---|
| `invalid_request`, `invalid_json` | 400 | The request body or parameters are not valid. |
| `text_too_short` | 400 | Plagiarism needs at least 30 words, AI detection at least 50. Nothing is charged. |
| `doc_too_long` | 400 | The text is longer than the account allows per check. |
| `unauthorized`, `invalid_api_key`, `key_revoked` | 401 | Missing, unknown or revoked API key. |
| `trial_exhausted` | 402 | The free API trial (2,000 words) cannot cover this request. Includes `wordsNeeded`, `trialRemaining`, `trialTotal` and `billingUrl`. Buy words or choose an API plan; test keys stay free. Nothing is charged. |
| `insufficient_words` | 402 | Not enough words across all sources. Includes `wordsNeeded`, `wordsAvailable`, `balances` and `billingUrl`. Nothing is charged. |
| `key_cap_reached` | 402 | This key's monthly word cap would be exceeded. Includes `wordsNeeded`, `monthlyWordCap` and `usedThisMonth`. Raise the cap in the developer portal. |
| `email_unverified` | 403 | Verify the account's email address first. |
| `forbidden` | 403 | The key cannot access this resource. |
| `not_found` | 404 | No such scan for this key. |
| `rate_limited` | 429 | Too many requests. The client exposes `Retry-After` as `RetryAfter`. |
| `internal_error` | 500 | Something went wrong on our side. |
| `workers_unavailable` | 503 | Checks are temporarily unavailable. Retry later. |
| `plan_required` | 403 | **Deprecated, no longer returned.** Free accounts now get a free API trial and free test keys. `ErrorCodes.PlanRequired` is kept (marked `[Obsolete]`, a warning only) so existing code still compiles. |

`WaitForScanAsync` throws `ScanTimeoutException` when it runs out of time. A scan whose status is `failed` is returned, not thrown, so check `status`.

## Notes

- **An AI score is a likelihood, not proof** that someone used AI. A similarity score does not decide on its own whether text is plagiarism. Review the result yourself, and do not use either score alone to accuse anyone.
- Test-key scans carry `"sandbox": true` and contain sample data, not a real check.
- Each tool you run is billed at the full word count.
- Plagiarism Checker API overview: https://plagiarismcheckerplus.com/integrations/api
- AI Detector API overview: https://plagiarismcheckerplus.com/ai-detector-api
- API pricing: https://plagiarismcheckerplus.com/api-pricing
- API reference (try requests in the browser): https://app.plagiarismcheckerplus.com/api-docs
- Developer portal (keys, usage, billing, webhooks): https://app.plagiarismcheckerplus.com/developers
- The OpenAPI spec this client follows is in [`spec/openapi.json`](spec/openapi.json).

## Other languages

| Language | Repository | Install |
|---|---|---|
| JavaScript / TypeScript | [plagiarism-checker-plus-js](https://github.com/pcp-tech/plagiarism-checker-plus-js) | `npm install plagiarism-checker-plus` |
| Python | [plagiarism-checker-plus-python](https://github.com/pcp-tech/plagiarism-checker-plus-python) | `pip install plagiarism-checker-plus` |
| PHP | [plagiarism-checker-plus-php](https://github.com/pcp-tech/plagiarism-checker-plus-php) | `composer require plagiarismcheckerplus/sdk` |
| Ruby | [plagiarism-checker-plus-ruby](https://github.com/pcp-tech/plagiarism-checker-plus-ruby) | `gem install plagiarism_checker_plus` |
| Go | [plagiarism-checker-plus-go](https://github.com/pcp-tech/plagiarism-checker-plus-go) | `go get github.com/pcp-tech/plagiarism-checker-plus-go` |
| Java | [plagiarism-checker-plus-java](https://github.com/pcp-tech/plagiarism-checker-plus-java) | Maven `com.plagiarismcheckerplus:sdk` |
| **C# / .NET** (this repository) | [plagiarism-checker-plus-dotnet](https://github.com/pcp-tech/plagiarism-checker-plus-dotnet) | `dotnet add package PlagiarismCheckerPlus` |

## License

MIT. Copyright (c) 2026 Plagiarism Checker Plus.
