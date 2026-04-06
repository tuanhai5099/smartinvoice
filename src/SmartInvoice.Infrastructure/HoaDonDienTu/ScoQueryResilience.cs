using System.Net;
using System.Net.Http.Headers;
using Polly;
using Polly.Retry;

namespace SmartInvoice.Infrastructure.HoaDonDienTu;

/// <summary>
/// Retry + jitter for sco-query host only. Avoids stacking with <see cref="ApiRetryHelper"/> (use one path per request).
/// </summary>
internal static class ScoQueryResilience
{
    private static readonly ResiliencePipeline<HttpResponseMessage> Pipeline = CreatePipeline();

    private static ResiliencePipeline<HttpResponseMessage> CreatePipeline()
    {
        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(900),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = static args =>
                {
                    if (args.Outcome.Exception is not null)
                    {
                        if (args.Outcome.Exception is OperationCanceledException oce && oce.CancellationToken.IsCancellationRequested)
                            return ValueTask.FromResult(false);
                        return ValueTask.FromResult(args.Outcome.Exception is HttpRequestException or TaskCanceledException);
                    }

                    var r = args.Outcome.Result;
                    if (r is null)
                        return ValueTask.FromResult(false);
                    var code = r.StatusCode;
                    if (code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                        return ValueTask.FromResult(true);
                    return ValueTask.FromResult((int)code >= 500);
                },
                OnRetry = static args =>
                {
                    args.Outcome.Result?.Dispose();
                    return default;
                }
            })
            .Build();
    }

    internal static bool IsScoQueryUrl(string url) =>
        url.Contains("sco-query", StringComparison.OrdinalIgnoreCase);

    /// <summary>GET with Bearer; new request per attempt; disposes non-success responses before retry inside Polly.</summary>
    internal static async Task<HttpResponseMessage> SendAuthorizedGetAsync(
        HttpClient client,
        string accessToken,
        string url,
        CancellationToken cancellationToken)
    {
        return await Pipeline.ExecuteAsync(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
