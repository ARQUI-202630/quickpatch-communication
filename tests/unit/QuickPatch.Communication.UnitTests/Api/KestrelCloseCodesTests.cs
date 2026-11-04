using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using QuickPatch.Communication.Api.Realtime;
using QuickPatch.Communication.UnitTests.Support;

namespace QuickPatch.Communication.UnitTests.Api;

/// <summary>
/// Códigos de cierre por plazo con Kestrel real (SCRUM-469). El servidor en memoria no aborta el socket al cancelar una
/// lectura, pero el WebSocket de Kestrel sí: si el plazo cancela la lectura, el socket queda abortado y la app recibe un
/// cierre sin código (1006) en lugar de 4002 o 4004.
/// </summary>
public sealed class KestrelCloseCodesTests : IClassFixture<KestrelCloseCodesTests.Factory>
{
    private readonly Factory _factory;

    public KestrelCloseCodesTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public Factory()
        {
            UseKestrel(0);
            StartServer();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Jwt:PublicKeyPem", TestTokens.PublicKeyPem);
            builder.UseSetting("Realtime:AuthTimeoutSeconds", "1");
        }
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var socket = new ClientWebSocket();
        var http = _factory.ClientOptions.BaseAddress;
        var uri = new UriBuilder(http) { Scheme = "ws", Path = "/v1/realtime" }.Uri;
        await socket.ConnectAsync(uri, CancellationToken.None);
        return socket;
    }

    /// <summary>Lee hasta la trama de cierre e indica su código; null si el servidor cortó sin cerrar.</summary>
    private static async Task<int?> CloseCodeAsync(WebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return (int?)result.CloseStatus;
                }
            }
        }
        catch (WebSocketException)
        {
            return null;
        }
    }

    [Fact]
    public async Task SinTramaAuth_CierraCon4002()
    {
        using var socket = await ConnectAsync();

        Assert.Equal(CloseCodes.NoAuthentication, await CloseCodeAsync(socket));
    }

    [Fact]
    public async Task TokenVencido_CierraCon4004()
    {
        using var socket = await ConnectAsync();
        var token = TestTokens.Create(Guid.NewGuid(), Guid.NewGuid(), "cliente", TimeSpan.FromSeconds(2));
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "auth", token })),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

        Assert.Equal(CloseCodes.TokenExpired, await CloseCodeAsync(socket));
    }
}