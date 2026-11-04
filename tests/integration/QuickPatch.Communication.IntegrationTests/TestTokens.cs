using System.Security.Cryptography;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace QuickPatch.Communication.IntegrationTests;

/// <summary>Llaves y tokens de prueba, como los emitiría Identity (RS256, ADR-018).</summary>
internal static class TestTokens
{
    public const string Issuer = "quickpatch-identity";
    public const string Audience = "quickpatch";

    public static RSA Key { get; } = RSA.Create(2048);

    public static string PublicKeyPem { get; } = Key.ExportSubjectPublicKeyInfoPem();

    public static string Create(
        Guid userId,
        Guid tenantId,
        string role,
        TimeSpan? lifetime = null,
        string issuer = Issuer,
        RSA? signingKey = null,
        bool includeTenant = true)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object> { ["sub"] = userId.ToString(), ["role"] = role };
        if (includeTenant)
        {
            claims["tenant_id"] = tenantId.ToString();
        }

        var expires = now + (lifetime ?? TimeSpan.FromMinutes(15));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = Audience,
            Claims = claims,
            NotBefore = expires < now ? expires.AddMinutes(-1) : now,
            IssuedAt = expires < now ? expires.AddMinutes(-1) : now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(signingKey ?? Key), SecurityAlgorithms.RsaSha256),
        });
    }
}