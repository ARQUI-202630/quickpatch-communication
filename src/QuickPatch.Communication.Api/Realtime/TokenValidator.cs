using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using QuickPatch.Transversal.Security;

namespace QuickPatch.Communication.Api.Realtime;

/// <summary>Usuario de la conexión, tomado solo de los claims del token (RN-U3).</summary>
public sealed record RealtimeUser(Guid UserId, Guid TenantId, string Role, DateTimeOffset ExpiresAt);

/// <summary>
/// Valida el JWT de la trama <c>auth</c>: firma RS256 con la llave pública de Identity, emisor, audiencia y vigencia
/// (ADR-018). Nunca registra el token.
/// </summary>
public sealed class TokenValidator
{
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };
    private readonly TokenValidationParameters? _parameters;

    public TokenValidator(IOptions<JwtSettings> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.PublicKeyPem))
        {
            return;
        }

        // Mismas reglas que el bearer del buzón (ADR-018), sin tolerancia de reloj.
        _parameters = JwtValidation.CreateParameters(settings.Issuer, settings.Audience, JwtValidation.LoadPublicKey(settings.PublicKeyPem), TimeSpan.Zero);
    }

    /// <summary>Devuelve el usuario si el token es válido; null si no lo es.</summary>
    public async Task<RealtimeUser?> ValidateAsync(string token)
    {
        if (_parameters is null || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var result = await _handler.ValidateTokenAsync(token, _parameters).ConfigureAwait(false);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            return null;
        }

        if (!Guid.TryParse(Claim(jwt, JwtClaims.Subject), out var userId) || userId == Guid.Empty
            || !Guid.TryParse(Claim(jwt, JwtClaims.Tenant), out var tenantId) || tenantId == Guid.Empty)
        {
            return null;
        }

        return new RealtimeUser(userId, tenantId, Claim(jwt, JwtClaims.Role) ?? string.Empty, new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero));
    }

    private static string? Claim(JsonWebToken jwt, string type) =>
        jwt.TryGetPayloadValue<string>(type, out var value) ? value : null;
}