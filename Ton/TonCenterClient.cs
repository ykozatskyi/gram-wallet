using System.Net;
using System.Text;
using System.Text.Json;
using GramWalletService.Configuration;
using GramWalletService.Ton.Models;

namespace GramWalletService.Ton;

public sealed class TonCenterClient
{
    public const string HttpClientName = "toncenter";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _httpClient;
    private readonly AppOptions _options;
    private readonly ILogger<TonCenterClient> _logger;

    public TonCenterClient(IHttpClientFactory httpClientFactory, AppOptions options, ILogger<TonCenterClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    internal TonCenterClient(HttpClient httpClient, AppOptions options, ILogger<TonCenterClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    internal Task<AccountStatesResponse> GetAccountStateAsync(string address, CancellationToken cancellationToken)
    {
        var url = "accountStates?address=" + Uri.EscapeDataString(address) + "&include_boc=false";
        return GetAsync<AccountStatesResponse>(url, cancellationToken);
    }

    internal Task<TransactionsResponse> GetTransactionsAsync(string address, int limit, int offset, CancellationToken cancellationToken)
    {
        var url = "transactions?account=" + Uri.EscapeDataString(address)
            + "&limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&sort=desc";
        return GetAsync<TransactionsResponse>(url, cancellationToken);
    }

    internal Task<WalletStatesResponse> GetWalletStatesAsync(string address, CancellationToken cancellationToken)
    {
        var url = "walletStates?address=" + Uri.EscapeDataString(address);
        return GetAsync<WalletStatesResponse>(url, cancellationToken);
    }

    internal Task<TransactionsResponse> GetTransactionsByMessageAsync(string messageHash, CancellationToken cancellationToken)
    {
        var url = "transactionsByMessage?msg_hash=" + Uri.EscapeDataString(messageHash)
            + "&limit=20&sort=desc";
        return GetAsync<TransactionsResponse>(url, cancellationToken);
    }

    internal Task<ActionsResponse> GetActionsByTransactionAsync(string transactionHash, CancellationToken cancellationToken)
    {
        var url = "actions?tx_hash=" + Uri.EscapeDataString(transactionHash) + "&limit=20&sort=desc";
        return GetAsync<ActionsResponse>(url, cancellationToken);
    }

    internal async Task<SendMessageResponse> SendBocAsync(string bocBase64, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["boc"] = bocBase64 }, TonJson.Options);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "message") { Content = content };
        var (status, body) = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (status == HttpStatusCode.OK)
        {
            try
            {
                return JsonSerializer.Deserialize<SendMessageResponse>(body, TonJson.Options) ?? new SendMessageResponse();
            }
            catch (JsonException)
            {
                throw new TonCenterException(TonCenterErrorKind.InvalidResponse, "TON Center returned an unexpected response.");
            }
        }

        if (status == HttpStatusCode.TooManyRequests)
            throw new TonCenterException(TonCenterErrorKind.RateLimited, "TON Center is rate limiting requests. Try again shortly.");

        if (IsDuplicate(status, body))
            throw new TonCenterException(TonCenterErrorKind.Duplicate, "TON Center already has this message.");

        if ((int)status >= 500 || status == 0)
            throw new TonCenterException(TonCenterErrorKind.Unavailable, "TON Center is unavailable.");

        _logger.LogWarning("TON Center rejected a payout message with status {StatusCode}.", (int)status);
        throw new TonCenterException(TonCenterErrorKind.Rejected, "TON Center rejected the signed message.", (int)status);
    }

    private async Task<T> GetAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        var (status, body) = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (status == HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning("TON Center rate limited a {Path} request.", PathOnly(relativeUrl));
            throw new TonCenterException(TonCenterErrorKind.RateLimited, "TON Center is rate limiting requests. Try again shortly.");
        }

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("TON Center rejected authentication with status {StatusCode}.", (int)status);
            throw new TonCenterException(TonCenterErrorKind.InvalidResponse, "TON Center rejected the API key.");
        }

        if (status != HttpStatusCode.OK)
        {
            _logger.LogWarning(
                "TON Center returned {StatusCode} for {Path}. Body: {Body}",
                (int)status,
                PathOnly(relativeUrl),
                ForLog(body));

            var kind = (int)status >= 500 || status == 0
                ? TonCenterErrorKind.Unavailable
                : TonCenterErrorKind.InvalidResponse;
            var message = kind == TonCenterErrorKind.Unavailable
                ? "TON Center is unavailable."
                : "TON Center rejected the request.";
            throw new TonCenterException(kind, message);
        }

        try
        {
            var payload = JsonSerializer.Deserialize<T>(body, TonJson.Options);
            if (payload is null)
                throw new TonCenterException(TonCenterErrorKind.InvalidResponse, "TON Center returned an empty response.");

            return payload;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("TON Center returned JSON that could not be parsed: {Message}", SecretRedactor.Redact(ex.Message, _options));
            throw new TonCenterException(TonCenterErrorKind.InvalidResponse, "TON Center returned an unexpected response.");
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = _httpClient ?? _httpClientFactory!.CreateClient(HttpClientName);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (response.StatusCode, body);
        }
        catch (TonCenterException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning("TON Center request timed out: {Message}", SecretRedactor.Redact(ex.Message, _options));
            throw new TonCenterException(TonCenterErrorKind.Timeout, "TON Center request timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("TON Center request failed: {Message}", SecretRedactor.Redact(ex.Message, _options));
            throw new TonCenterException(TonCenterErrorKind.Unavailable, "TON Center is unavailable.");
        }
    }

    private static bool IsDuplicate(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.Conflict)
            return true;

        return status == HttpStatusCode.BadRequest
            && body.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
    }

    private string ForLog(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return string.Empty;

        var text = body.Length <= 300 ? body : body[..300];
        return SecretRedactor.Redact(text, _options).Replace('\r', ' ').Replace('\n', ' ');
    }

    private static string PathOnly(string relativeUrl)
    {
        var query = relativeUrl.IndexOf('?');
        return query < 0 ? relativeUrl : relativeUrl[..query];
    }
}
