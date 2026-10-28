using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using QuickPatch.Communication.Api.Realtime;
using QuickPatch.Communication.Domain.Realtime;
using QuickPatch.Communication.UnitTests.Support;

namespace QuickPatch.Communication.UnitTests.Api;

public sealed class TokenValidatorTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Tenant = Guid.NewGuid();
    private readonly TokenValidator _validator = new(Options.Create(new JwtSettings { PublicKeyPem = TestTokens.PublicKeyPem }));

    [Fact]
    public async Task TokenValido_DevuelveUsuarioTenantYRolDelToken()
    {
        var user = await _validator.ValidateAsync(TestTokens.Create(User, Tenant, Roles.Tecnico));

        Assert.NotNull(user);
        Assert.Equal(User, user.UserId);
        Assert.Equal(Tenant, user.TenantId);
        Assert.Equal(Roles.Tecnico, user.Role);
        Assert.True(user.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task TokensInvalidos_DevuelvenNull()
    {
        using var otraLlave = RSA.Create(2048);
        Assert.Null(await _validator.ValidateAsync(TestTokens.Create(User, Tenant, Roles.Cliente, lifetime: TimeSpan.FromMinutes(-5))));
        Assert.Null(await _validator.ValidateAsync(TestTokens.Create(User, Tenant, Roles.Cliente, issuer: "otro-emisor")));
        Assert.Null(await _validator.ValidateAsync(TestTokens.Create(User, Tenant, Roles.Cliente, signingKey: otraLlave)));
        Assert.Null(await _validator.ValidateAsync(TestTokens.Create(User, Tenant, Roles.Cliente, includeTenant: false)));
        Assert.Null(await _validator.ValidateAsync("no-es-un-jwt"));
        Assert.Null(await _validator.ValidateAsync(" "));
    }

    [Fact]
    public async Task SinLlavePublica_NingunTokenEsValido()
    {
        var sinLlave = new TokenValidator(Options.Create(new JwtSettings()));
        Assert.Null(await sinLlave.ValidateAsync(TestTokens.Create(User, Tenant, Roles.Cliente)));
    }

    [Theory]
    [InlineData("{\"type\":\"auth\",\"token\":\"abc\"}", "abc")]
    [InlineData("{\"type\":\"auth\",\"token\":\"\"}", null)]
    [InlineData("{\"type\":\"auth\"}", null)]
    [InlineData("{\"type\":\"hola\",\"token\":\"abc\"}", null)]
    [InlineData("{\"type\":\"auth\",\"token\":\"abc\",\"tenantId\":\"x\"}", null)]
    [InlineData("{\"type\":\"auth\",\"token\":5}", null)]
    [InlineData("[]", null)]
    [InlineData("no es json", null)]
    public void LecturaDeLaTramaAuth(string frame, string? expected) =>
        Assert.Equal(expected, RealtimeSession.ReadAuthToken(frame));
}