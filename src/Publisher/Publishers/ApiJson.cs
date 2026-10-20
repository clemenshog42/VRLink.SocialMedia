using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VRLinkPublisher.Publishers;

internal static class ApiJson
{
    public static bool IsRetryable(HttpStatusCode code) =>
        code == HttpStatusCode.TooManyRequests || (int)code >= 500 || code == HttpStatusCode.RequestTimeout;

    public static async Task<JsonNode> ReadAsync(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? node = null;
        try { node = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body); } catch (JsonException) { }
        if (!resp.IsSuccessStatusCode)
        {
            var msg = node?["error"]?["message"]?.GetValue<string>() ?? Trunc(body);
            var code = node?["error"]?["code"]?.ToString();
            throw new PlatformApiException($"{what}: HTTP {(int)resp.StatusCode} {code} {msg}".Trim(), IsRetryable(resp.StatusCode), (int)resp.StatusCode);
        }
        return node ?? new JsonObject();
    }

    public static string Trunc(string s, int n = 500) => s.Length <= n ? s : s[..n] + "...";

    public static long Long(JsonNode? n) => n == null ? 0 : n.GetValueKind() switch
    {
        JsonValueKind.Number => n.GetValue<long>(),
        JsonValueKind.String => long.TryParse(n.GetValue<string>(), out var v) ? v : 0,
        _ => 0,
    };
}
