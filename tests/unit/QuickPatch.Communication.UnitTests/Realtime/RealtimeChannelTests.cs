using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using QuickPatch.Communication.Api.Realtime;
using QuickPatch.Communication.Application.Realtime;

namespace QuickPatch.Communication.UnitTests.Realtime;

/// <summary>
/// Pruebas del canal <c>/v1/realtime</c> con un cliente WebSocket real contra el servicio en memoria, según
/// <c>asyncapi/realtime.v1.yaml</c>: autenticación, códigos de cierre y entrega aislada por usuario y tenant.
/// </summary>
public sealed class RealtimeChannelTests : IClassFixture<RealtimeChannelTests.Factory>
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private readonly Factory _factory;

    public RealtimeChannelTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Jwt:PublicKeyPem", TestTokens.PublicKeyPem);
            builder.UseSetting("Realtime:AuthTimeoutSeconds", "1");
        }
    }

    private async Task<WebSocket> ConnectAsync()
    {
        var client = _factory.Server.CreateWebSocketClient();
        return await client.ConnectAsync(new Uri("ws://localhost/v1/realtime"), CancellationToken.None);
    }

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static Task AuthAsync(WebSocket socket, string token) =>
        SendAsync(socket, JsonSerializer.Serialize(new { type = "auth", token }));

    /// <summary>Lee la siguiente trama; devuelve null si no llega nada en el plazo.</summary>
    private static async Task<(WebSocketMessageType Type, string Text, int? CloseCode)?> ReceiveAsync(WebSocket socket, TimeSpan timeout)
    {
        var buffer = new byte[16 * 1024];
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var result = await socket.ReceiveAsync(buffer, cts.Token);
            return (result.MessageType, Encoding.UTF8.GetString(buffer, 0, result.Count), (int?)result.CloseStatus);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<WebSocket> ConnectAuthenticatedAsync(Guid user, Guid tenant, string role, TimeSpan? lifetime = null)
    {
        var socket = await ConnectAsync();
        await AuthAsync(socket, TestTokens.Create(user, tenant, role, lifetime));
        var accepted = await ReceiveAsync(socket, TimeSpan.FromSeconds(5));
        Assert.NotNull(accepted);
        using var json = JsonDocument.Parse(accepted.Value.Text);
        Assert.Equal("auth.accepted", json.RootElement.GetProperty("type").GetString());
        Assert.True(json.RootElement.TryGetProperty("expiresAt", out _));
        return socket;
    }

    private static string Event(string type, Guid tenant, object data) => JsonSerializer.Serialize(new
    {
        eventId = Guid.NewGuid(),
        eventType = type,
        eventVersion = 1,
        occurredAt = "2026-10-12T16:10:00Z",
        correlationId = Guid.NewGuid(),
        tenantId = tenant,
        producer = "matching-service",
        data,
    });

    private async Task PublishAsync(string message)
    {
        var notifier = _factory.Services.GetRequiredService<RealtimeNotifier>();
        Assert.Equal(NotifyResult.Delivered, await notifier.HandleAsync(message, CancellationToken.None));
    }

    [Fact]
    public async Task AvisoDeEstado_LlegaAlClienteYAlTecnico_YNuncaAOtroTenant()
    {
        var client = Guid.NewGuid();
        var technician = Guid.NewGuid();
        var request = Guid.NewGuid();
        using var clientSocket = await ConnectAuthenticatedAsync(client, TenantA, "cliente");
        using var technicianSocket = await ConnectAuthenticatedAsync(technician, TenantA, "tecnico");
        using var adminSocket = await ConnectAuthenticatedAsync(Guid.NewGuid(), TenantA, "admin_tenant");
        // Mismo usuario y mismo rol, pero de otro tenant: nunca debe recibir el aviso (ADR-005, DD 8.5).
        using var otherTenantClient = await ConnectAuthenticatedAsync(client, TenantB, "cliente");
        using var otherTenantAdmin = await ConnectAuthenticatedAsync(Guid.NewGuid(), TenantB, "admin_tenant");

        await PublishAsync(Event(EventTypes.ServiceRequestStatusChanged, TenantA,
            new { serviceRequestId = request, clientId = client, technicianId = technician, previousStatus = "buscando_tecnico", status = "asignado", changedAt = "2026-10-12T16:10:00Z" }));

        foreach (var socket in new[] { clientSocket, technicianSocket })
        {
            var message = await ReceiveAsync(socket, TimeSpan.FromSeconds(5));
            Assert.NotNull(message);
            using var json = JsonDocument.Parse(message.Value.Text);
            Assert.Equal("service-request.status-changed", json.RootElement.GetProperty("type").GetString());
            Assert.Equal(request, json.RootElement.GetProperty("serviceRequestId").GetGuid());
            Assert.Equal("asignado", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(4, json.RootElement.EnumerateObject().Count());
        }

        var counts = await ReceiveAsync(adminSocket, TimeSpan.FromSeconds(5));
        Assert.NotNull(counts);
        Assert.Contains("service-request.counts-changed", counts.Value.Text, StringComparison.Ordinal);

        Assert.Null(await ReceiveAsync(otherTenantClient, TimeSpan.FromMilliseconds(500)));
        Assert.Null(await ReceiveAsync(otherTenantAdmin, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task OfertaNueva_SoloLlegaAlTecnicoDeLaOferta()
    {
        var technician = Guid.NewGuid();
        using var socket = await ConnectAuthenticatedAsync(technician, TenantA, "tecnico");
        using var otherTechnician = await ConnectAuthenticatedAsync(Guid.NewGuid(), TenantA, "tecnico");

        await PublishAsync(Event(EventTypes.OfferCreated, TenantA,
            new { serviceRequestId = Guid.NewGuid(), attemptId = Guid.NewGuid(), technicianId = technician, expiresAt = "2026-10-12T16:13:00Z" }));

        var message = await ReceiveAsync(socket, TimeSpan.FromSeconds(5));
        Assert.NotNull(message);
        Assert.Contains("\"type\":\"offer.created\"", message.Value.Text, StringComparison.Ordinal);
        Assert.Null(await ReceiveAsync(otherTechnician, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task SinTramaDeAutenticacion_Cierra4002()
    {
        using var socket = await ConnectAsync();
        var message = await ReceiveAsync(socket, TimeSpan.FromSeconds(5));
        Assert.Equal(CloseCodes.NoAuthentication, message?.CloseCode);
    }

    [Fact]
    public async Task TokenInvalido_Cierra4001()
    {
        using var socket = await ConnectAsync();
        await AuthAsync(socket, "no-es-un-jwt");
        Assert.Equal(CloseCodes.InvalidToken, (await ReceiveAsync(socket, TimeSpan.FromSeconds(5)))?.CloseCode);
    }

    [Fact]
    public async Task TokenVencido_Cierra4001()
    {
        using var socket = await ConnectAsync();
        await AuthAsync(socket, TestTokens.Create(Guid.NewGuid(), TenantA, "cliente", TimeSpan.FromMinutes(-5)));
        Assert.Equal(CloseCodes.InvalidToken, (await ReceiveAsync(socket, TimeSpan.FromSeconds(5)))?.CloseCode);
    }

    [Theory]
    [InlineData("hola")]
    [InlineData("{\"type\":\"auth\",\"token\":\"x\",\"tenantId\":\"00000000-0000-0000-0000-000000000001\"}")]
    public async Task TramaInvalida_Cierra4000(string frame)
    {
        using var socket = await ConnectAsync();
        await SendAsync(socket, frame);
        Assert.Equal(CloseCodes.InvalidFrame, (await ReceiveAsync(socket, TimeSpan.FromSeconds(5)))?.CloseCode);
    }

    [Fact]
    public async Task TramaMuyGrande_Cierra1009()
    {
        using var socket = await ConnectAsync();
        await SendAsync(socket, new string('a', 9000));
        Assert.Equal(CloseCodes.FrameTooBig, (await ReceiveAsync(socket, TimeSpan.FromSeconds(5)))?.CloseCode);
    }

    [Theory]
    [InlineData("admin_plataforma")]
    [InlineData("proveedor")]
    public async Task RolQueNoUsaElCanal_Cierra4003(string role)
    {
        using var socket = await ConnectAsync();
        await AuthAsync(socket, TestTokens.Create(Guid.NewGuid(), TenantA, role));
        Assert.Equal(CloseCodes.RoleNotAllowed, (await ReceiveAsync(socket, TimeSpan.FromSeconds(5)))?.CloseCode);
    }

    [Fact]
    public async Task TramaDespuesDeAutenticarse_Cierra4000()
    {
        using var socket = await ConnectAuthenticatedAsync(Guid.NewGuid(), TenantA, "cliente");
        await SendAsync(socket, "{\"type\":\"ping\"}");
        Assert.Equal(CloseCodes.InvalidFrame, (await ReceiveAsync(socket, TimeSpan.FromSeconds(5)))?.CloseCode);
    }

    [Fact]
    public async Task TokenQueVenceDuranteLaConexion_Cierra4004()
    {
        using var socket = await ConnectAuthenticatedAsync(Guid.NewGuid(), TenantA, "cliente", TimeSpan.FromSeconds(3));
        Assert.Equal(CloseCodes.TokenExpired, (await ReceiveAsync(socket, TimeSpan.FromSeconds(10)))?.CloseCode);
    }

    [Fact]
    public async Task AppCierraLaConexion_SeLiberaDelRegistro()
    {
        var registry = _factory.Services.GetRequiredService<ConnectionRegistry>();
        using var socket = await ConnectAuthenticatedAsync(Guid.NewGuid(), TenantA, "cliente");
        var before = registry.Count;
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "adios", CancellationToken.None);
        await ReceiveAsync(socket, TimeSpan.FromSeconds(5));
        for (var i = 0; i < 50 && registry.Count >= before; i++)
        {
            await Task.Delay(50);
        }

        Assert.True(registry.Count < before);
    }

    [Fact]
    public async Task PeticionHttpNormal_Responde400()
    {
        using var http = _factory.CreateClient();
        var response = await http.GetAsync(new Uri("/v1/realtime", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}