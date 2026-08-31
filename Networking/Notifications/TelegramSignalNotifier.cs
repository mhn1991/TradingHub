using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Networking.Notifications;

/// <summary>
/// Sends signal alerts to a Telegram chat or group through the Bot API's <c>sendMessage</c>.
/// <para>
/// Failures are contained by design — see <see cref="ISignalNotifier"/>. Every send is wrapped:
/// a timeout, a DNS failure, a revoked token or a 429 returns <see langword="false"/> and invokes
/// <see cref="OnError"/>, and never propagates into the agent that raised the signal.
/// </para>
/// </summary>
public sealed class TelegramSignalNotifier : ISignalNotifier, IDisposable
{
    private readonly TelegramNotifierOptions _options;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentlySent = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly Uri _endpoint;

    /// <summary>
    /// Invoked when a send fails. Deliberately a callback rather than a logger dependency, so this
    /// project stays a dependency-free leaf; hosts wire it to their own logging.
    /// </summary>
    public Action<string, Exception?>? OnError { get; init; }

    public TelegramSignalNotifier(
        TelegramNotifierOptions options,
        HttpClient? client = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownsClient = client is null;
        _client = client ?? new HttpClient();
        // Never mutate a caller-supplied client. It may be pooled, shared, or - as in the probe
        // CLI - already have issued a request, after which HttpClient throws on any property set
        // ("This instance has already started one or more requests"). Only a client we created
        // ourselves is safe to configure. Requests use an absolute uri, so a BaseAddress on the
        // supplied client is irrelevant either way.
        if (_ownsClient)
        {
            _client.BaseAddress = options.BaseAddress;
            _client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        }

        // Built as an ABSOLUTE uri on purpose. Every real bot token contains a colon
        // ("<botid>:<hash>"), and in a *relative* uri that leading "bot123456:" parses as a
        // scheme - collapsing the path to "<hash>/sendMessage" and posting to the wrong place.
        // Colons are legal inside the path of an absolute uri, so this form is unambiguous.
        _endpoint = new Uri(
            $"{options.BaseAddress.AbsoluteUri.TrimEnd('/')}/bot{options.BotToken}/sendMessage",
            UriKind.Absolute);
    }

    public async ValueTask<bool> NotifyAsync(
        SignalNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!_options.Enabled)
            return false;
        if (IsDuplicate(notification))
            return false;

        try
        {
            var request = new SendMessageRequest(
                _options.ChatId,
                TelegramMessageFormatter.Format(notification),
                "HTML",
                DisableWebPagePreview: true);

            using HttpResponseMessage response = await _client
                .PostAsJsonAsync(
                    _endpoint,
                    request,
                    TelegramJsonContext.Default.SendMessageRequest,
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return true;

            // The body carries Telegram's own description ("chat not found", "bot was blocked"),
            // which is the only way to tell a misconfiguration from a transient failure.
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            OnError?.Invoke($"Telegram rejected the alert with {(int)response.StatusCode}: {Truncate(body)}", null);
            MarkNotSent(notification);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown, not a delivery fault — let it propagate as cancellation semantics.
            MarkNotSent(notification);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            OnError?.Invoke("Telegram alert could not be delivered.", exception);
            MarkNotSent(notification);
            return false;
        }
    }

    /// <summary>
    /// Claims the deduplication slot up front so two threads racing on the same signal cannot both
    /// send. A failed send releases it again via <see cref="MarkNotSent"/> so a retry is possible.
    /// </summary>
    private bool IsDuplicate(SignalNotification notification)
    {
        if (_options.DeduplicationMinutes <= 0)
            return false;

        DateTimeOffset now = _timeProvider.GetUtcNow();
        var window = TimeSpan.FromMinutes(_options.DeduplicationMinutes);
        if (_recentlySent.Count > 512)
        {
            foreach ((string key, DateTimeOffset sentAt) in _recentlySent)
            {
                if (now - sentAt > window)
                    _recentlySent.TryRemove(key, out _);
            }
        }

        string dedupKey = notification.DeduplicationKey;
        if (_recentlySent.TryGetValue(dedupKey, out DateTimeOffset previous) && now - previous <= window)
            return true;
        _recentlySent[dedupKey] = now;
        return false;
    }

    private void MarkNotSent(SignalNotification notification) =>
        _recentlySent.TryRemove(notification.DeduplicationKey, out _);

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    internal sealed record SendMessageRequest(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("parse_mode")] string ParseMode,
        [property: JsonPropertyName("disable_web_page_preview")] bool DisableWebPagePreview);
}

/// <summary>Source-generated so the notifier stays AOT-compatible with the rest of this project.</summary>
[JsonSerializable(typeof(TelegramSignalNotifier.SendMessageRequest))]
internal sealed partial class TelegramJsonContext : JsonSerializerContext;
