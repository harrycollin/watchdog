using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KioskWatchdog.Core.Notifications;

public sealed class HttpWebhookClient : IWebhookClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly HttpClient _http;

    public HttpWebhookClient(HttpClient http)
    {
        _http = http;
    }

    public async Task PostAsync(
        string url,
        WebhookPayload payload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        using var response = await _http.PostAsJsonAsync(url, payload, SerializerOptions, cts.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
