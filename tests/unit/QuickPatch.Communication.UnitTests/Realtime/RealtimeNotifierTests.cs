using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.UnitTests.Realtime;

public class RealtimeNotifierTests
{
    private sealed class FakeConnections : IRealtimeConnections
    {
        public List<Delivery> Deliveries { get; } = [];

        public Task<int> DeliverAsync(Delivery delivery, CancellationToken cancellationToken)
        {
            Deliveries.Add(delivery);
            return Task.FromResult(1);
        }
    }

    private static string Message(Guid eventId, string type = EventTypes.SearchResumed) => JsonSerializer.Serialize(new
    {
        eventId,
        eventType = type,
        eventVersion = 1,
        occurredAt = "2026-10-12T16:10:00Z",
        correlationId = Guid.NewGuid(),
        tenantId = Guid.NewGuid(),
        producer = "matching-service",
        data = new { serviceRequestId = Guid.NewGuid(), clientId = Guid.NewGuid(), trigger = "aprobacion" },
    });

    [Fact]
    public async Task EventoValido_EntregaYUnRepetidoSeDescarta()
    {
        var connections = new FakeConnections();
        var notifier = new RealtimeNotifier(connections, new RecentEventIds(), NullLogger<RealtimeNotifier>.Instance);
        var id = Guid.NewGuid();

        Assert.Equal(NotifyResult.Delivered, await notifier.HandleAsync(Message(id), CancellationToken.None));
        Assert.Equal(2, connections.Deliveries.Count);
        Assert.Equal(NotifyResult.Duplicate, await notifier.HandleAsync(Message(id), CancellationToken.None));
        Assert.Equal(2, connections.Deliveries.Count);
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("null")]
    [InlineData("{\"eventType\":\"matching.search-resumed\"}")]
    [InlineData("{\"eventId\":\"5b1f0c3a-0000-4000-8000-000000000001\",\"eventType\":\"matching.search-resumed\",\"eventVersion\":1,\"occurredAt\":\"2026-10-12T16:10:00Z\",\"tenantId\":\"5b1f0c3a-0000-4000-8000-000000000002\",\"producer\":\"matching-service\",\"data\":{}}")]
    public async Task MensajeFueraDeContrato_SeDescartaSinEntregar(string message)
    {
        var connections = new FakeConnections();
        var notifier = new RealtimeNotifier(connections, new RecentEventIds(), NullLogger<RealtimeNotifier>.Instance);

        Assert.Equal(NotifyResult.Discarded, await notifier.HandleAsync(message, CancellationToken.None));
        Assert.Empty(connections.Deliveries);
    }

    [Fact]
    public void RecentEventIds_OlvidaLosMasViejosAlLlenarse()
    {
        var ids = new RecentEventIds(capacity: 2);
        var a = Guid.NewGuid();
        Assert.True(ids.TryAdd(a));
        Assert.False(ids.TryAdd(a));
        Assert.True(ids.TryAdd(Guid.NewGuid()));
        Assert.True(ids.TryAdd(Guid.NewGuid()));
        Assert.True(ids.TryAdd(a));
    }
}