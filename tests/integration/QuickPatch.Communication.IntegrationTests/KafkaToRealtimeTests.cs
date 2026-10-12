using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using Confluent.Kafka;
using Confluent.Kafka.Admin;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

using QuickPatch.Communication.Application.Realtime;

using Testcontainers.Kafka;

namespace QuickPatch.Communication.IntegrationTests;

/// <summary>
/// De punta a punta con un Kafka real: un evento publicado en su topic llega como aviso al WebSocket del cliente que
/// corresponde, y no al de otro tenant (ADR-023, DD 8.5).
/// </summary>
public sealed class KafkaToRealtimeTests : IAsyncLifetime
{
    private readonly KafkaContainer _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.7.1").Build();
    private WebApplicationFactory<Program>? _factory;

    public async Task InitializeAsync()
    {
        await _kafka.StartAsync();
        // Los topics se crean antes de arrancar el servicio, como en QA y producción (quickpatch-kafka/topics).
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _kafka.GetBootstrapAddress() }).Build();
        await admin.CreateTopicsAsync(EventTypes.Consumed.Select(t => new TopicSpecification { Name = t, NumPartitions = 1, ReplicationFactor = 1 }));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Jwt:PublicKeyPem", TestTokens.PublicKeyPem);
            builder.UseSetting("Kafka:BootstrapServers", _kafka.GetBootstrapAddress());
            builder.UseSetting("Kafka:ConsumerGroupId", "communication-service-pruebas");
        });
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _kafka.DisposeAsync();
    }

    [Fact]
    public async Task EventoDeKafka_LlegaComoAvisoSoloAlClienteDelTenant()
    {
        var tenant = Guid.NewGuid();
        var client = Guid.NewGuid();
        var request = Guid.NewGuid();
        using var clientSocket = await ConnectAsync(client, tenant);
        using var otherTenant = await ConnectAsync(client, Guid.NewGuid());

        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = _kafka.GetBootstrapAddress() }).Build();

        // El consumidor arranca leyendo desde el final del topic: se publica hasta que tenga asignada la partición.
        string? aviso = null;
        for (var intento = 0; intento < 30 && aviso is null; intento++)
        {
            var evento = JsonSerializer.Serialize(new
            {
                eventId = Guid.NewGuid(),
                eventType = EventTypes.NoTechnicianAvailable,
                eventVersion = 1,
                occurredAt = "2026-10-12T16:10:00Z",
                correlationId = Guid.NewGuid(),
                tenantId = tenant,
                producer = "matching-service",
                data = new { serviceRequestId = request, clientId = client, attemptsCount = 3 },
            });
            await producer.ProduceAsync(EventTypes.NoTechnicianAvailable, new Message<string, string> { Key = request.ToString(), Value = evento });
            aviso = await ReceiveAsync(clientSocket, TimeSpan.FromSeconds(1));
        }

        Assert.NotNull(aviso);
        using var json = JsonDocument.Parse(aviso);
        Assert.Equal("service-request.status-changed", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("en_espera", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(request, json.RootElement.GetProperty("serviceRequestId").GetGuid());
        Assert.Null(await ReceiveAsync(otherTenant, TimeSpan.FromMilliseconds(500)));
    }

    private async Task<WebSocket> ConnectAsync(Guid user, Guid tenant)
    {
        var socket = await _factory!.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/v1/realtime"), CancellationToken.None);
        var auth = JsonSerializer.Serialize(new { type = "auth", token = TestTokens.Create(user, tenant, "cliente") });
        await socket.SendAsync(Encoding.UTF8.GetBytes(auth), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Contains("auth.accepted", await ReceiveAsync(socket, TimeSpan.FromSeconds(5)) ?? string.Empty, StringComparison.Ordinal);
        return socket;
    }

    private static async Task<string?> ReceiveAsync(WebSocket socket, TimeSpan timeout)
    {
        var buffer = new byte[16 * 1024];
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var result = await socket.ReceiveAsync(buffer, cts.Token);
            return Encoding.UTF8.GetString(buffer, 0, result.Count);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}