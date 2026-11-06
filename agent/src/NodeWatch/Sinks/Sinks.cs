using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace NodeWatch.Sinks;

internal enum Outcome { Ok, Retry, Drop }

internal sealed record SendResult(Outcome Outcome, string Message)
{
    public static readonly SendResult Success = new(Outcome.Ok, "ok");
}

internal interface ISink
{
    string Name { get; }

    /// <summary>Lowest severity it gets (0 info, 1 warning, 2 critical). A resolved event goes where its firing went.</summary>
    int MinRank { get; }

    Task<SendResult> SendAsync(CloudEvent e, CancellationToken ct);
}

internal static class Http
{
    /// <summary>2xx: ok; 408, 429 and 5xx: retry later; other 4xx: the request itself is wrong, retrying will not help.</summary>
    public static async Task<SendResult> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return SendResult.Success;
            var body = await SafeBody(response, ct);
            var code = (int)response.StatusCode;
            var outcome = code is 408 or 429 || code >= 500 ? Outcome.Retry : Outcome.Drop;
            return new SendResult(outcome, $"HTTP {code}: {body}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new SendResult(Outcome.Retry, e.Message);
        }
    }

    private static async Task<string> SafeBody(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            return text.Length > 300 ? text[..300] + "…" : text;
        }
        catch (Exception)
        {
            return "";
        }
    }

    public static StringContent CloudEventContent(CloudEvent e) =>
        new(e.ToJson(), Encoding.UTF8, new MediaTypeHeaderValue("application/cloudevents+json") { CharSet = "utf-8" });
}

/// <summary>Telegram Bot API sendMessage (https://core.telegram.org/bots/api#sendmessage).</summary>
internal sealed class TelegramSink(HttpClient http, string name, int minRank, string botToken, string chatId) : ISink
{
    public string Name => name;
    public int MinRank => minRank;

    public Task<SendResult> SendAsync(CloudEvent e, CancellationToken ct)
    {
        var text = $"{EventText.Icon(e)} <b>{WebUtility.HtmlEncode(EventText.Headline(e))}</b>";
        if (!string.IsNullOrEmpty(e.Data.Text)) text += "\n" + WebUtility.HtmlEncode(e.Data.Text);
        if (text.Length > 4000) text = text[..4000];
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.telegram.org/bot{botToken}/sendMessage")
        {
            Content = JsonContent.Create(new
            {
                chat_id = chatId,
                text,
                parse_mode = "HTML",
                // Warnings and recoveries arrive silently; only critical alerts make a sound.
                disable_notification = e.IsResolved || e.Data.Severity != "critical",
            }),
        };
        return Http.SendAsync(http, request, ct);
    }
}

/// <summary>Matrix client-server API: an m.notice into one room. The event id is the transaction id, so a retry
/// never posts twice.</summary>
internal sealed class MatrixSink(HttpClient http, string name, int minRank, string homeserver, string roomId, string accessToken)
    : ISink
{
    public string Name => name;
    public int MinRank => minRank;

    public Task<SendResult> SendAsync(CloudEvent e, CancellationToken ct)
    {
        var plain = EventText.Headline(e) + (string.IsNullOrEmpty(e.Data.Text) ? "" : "\n" + e.Data.Text);
        var html = $"{EventText.Icon(e)} <b>{WebUtility.HtmlEncode(EventText.Headline(e))}</b>" +
                   (string.IsNullOrEmpty(e.Data.Text) ? "" : "<br>" + WebUtility.HtmlEncode(e.Data.Text));
        var url = $"{homeserver.TrimEnd('/')}/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/send/m.room.message/" +
                  Uri.EscapeDataString(e.Id);
        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(new
            {
                msgtype = "m.notice",
                body = plain,
                format = "org.matrix.custom.html",
                formatted_body = html,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return Http.SendAsync(http, request, ct);
    }
}

/// <summary>SafeChat integration alerts (POST /api/ops/alerts, docs/integration-alerts-design.md). Heartbeats never go
/// here.</summary>
internal sealed class SafeChatSink(HttpClient http, string name, int minRank, string url, string token) : ISink
{
    public string Name => name;
    public int MinRank => minRank;

    public Task<SendResult> SendAsync(CloudEvent e, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = Http.CloudEventContent(e) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-SafeChat", "1");
        return Http.SendAsync(http, request, ct);
    }
}

/// <summary>The relay service: gets every event, heartbeats included, with the node token.</summary>
internal sealed class RelaySink(HttpClient http, string url, string nodeToken) : ISink
{
    public string Name => "relay";
    public int MinRank => 0;

    public Task<SendResult> SendAsync(CloudEvent e, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = Http.CloudEventContent(e) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nodeToken);
        return Http.SendAsync(http, request, ct);
    }
}

internal static class SinkFactory
{
    public static ISink Create(SinkOptions o, Config config, HttpClient http)
    {
        Severity.TryParse(o.MinSeverity, out var rank);
        return o.Type switch
        {
            "telegram" => new TelegramSink(http, o.DisplayName, rank, config.ReadSecret(o.BotTokenFile!), o.ChatId!),
            "matrix" => new MatrixSink(http, o.DisplayName, rank, o.Homeserver!, o.RoomId!, config.ReadSecret(o.AccessTokenFile!)),
            "safechat" => new SafeChatSink(http, o.DisplayName, rank, o.Url!, config.ReadSecret(o.TokenFile!)),
            _ => throw new ConfigException($"Ismeretlen kimenet-típus: {o.Type}"),
        };
    }
}
