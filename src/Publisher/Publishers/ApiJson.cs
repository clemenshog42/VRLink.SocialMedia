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
            // "error" is an object (Graph/Google API: {code,message,errors:[{reason}]}) or a plain string (OAuth: {error, error_description}).
            string? msg, code;
            if (node?["error"] is JsonObject eo)
            {
                msg = eo["message"]?.ToString() ?? Trunc(body);
                code = eo["code"]?.ToString();
                var reason = (eo["errors"] as JsonArray)?.FirstOrDefault()?["reason"]?.ToString();
                if (reason != null) code = $"{code}/{reason}";
            }
            else if (node?["error"] is JsonValue ev)
            {
                code = ev.ToString();
                msg = node?["error_description"]?.ToString() ?? Trunc(body);
            }
            else { msg = Trunc(body); code = null; }
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
