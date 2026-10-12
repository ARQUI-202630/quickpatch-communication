using System.Security.Cryptography;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

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

        // La llave vive lo que vive la aplicación: Microsoft.IdentityModel guarda en caché el proveedor de firma por
        // la huella de la llave, y liberar el RSA dejaría en la caché un proveedor inservible.
#pragma warning disable CA2000
        var rsa = RSA.Create();
#pragma warning restore CA2000
        rsa.ImportFromPem(settings.PublicKeyPem);
        _parameters = new TokenValidationParameters
        {
            ValidIssuer = settings.Issuer,
            ValidAudience = settings.Audience,
            IssuerSigningKey = new RsaSecurityKey(rsa),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.Zero,
        };
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

        if (!Guid.TryParse(Claim(jwt, "sub"), out var userId) || userId == Guid.Empty
            || !Guid.TryParse(Claim(jwt, "tenant_id"), out var tenantId) || tenantId == Guid.Empty)
        {
            return null;
        }

        return new RealtimeUser(userId, tenantId, Claim(jwt, "role") ?? string.Empty, new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero));
    }

    private static string? Claim(JsonWebToken jwt, string type) =>
        jwt.TryGetPayloadValue<string>(type, out var value) ? value : null;
}