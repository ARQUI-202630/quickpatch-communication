using System.Text.Json;

namespace QuickPatch.Communication.Api.Realtime;

/// <summary>Trama <c>auth</c> de <c>asyncapi/realtime.v1.yaml</c>: exactamente <c>type</c> y <c>token</c>.</summary>
public static class AuthFrame
{
    /// <summary>Devuelve el token, o <c>null</c> si la trama no cumple el contrato.</summary>
    public static string? ReadToken(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "auth"
                || !root.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = token.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}