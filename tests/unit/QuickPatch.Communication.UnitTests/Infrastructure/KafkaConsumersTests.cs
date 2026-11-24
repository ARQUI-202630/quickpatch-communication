using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Realtime;
using QuickPatch.Communication.Infrastructure.Messaging;

namespace QuickPatch.Communication.UnitTests.Infrastructure;

/// <summary>
/// Los dos consumidores sobre el bucle común (ADR-020): lo guardado se reintenta hasta que se guarda; el aviso en tiempo
/// real que falla no se reintenta (ADR-023) y el offset avanza igual.
/// </summary>
public class KafkaConsumersTests
{
    [Fact]
    public async Task Persistente_SiGuardarFalla_ReintentaElMismoMensajeHastaGuardarlo()
    {
        var consumer = FakeConsumer.Create("m1");
        var intentos = 0;
        using var services = new ServiceCollection().BuildServiceProvider();
        var service = new PersistentEventsConsumer("buzon", consumer, ["x"], (_, value, _) =>
        {
            Assert.Equal("m1", value);
            return ++intentos == 1 ? throw new InvalidOperationException("base caída") : Task.CompletedTask;
        }, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<PersistentEventsConsumer>.Instance, TimeSpan.FromMilliseconds(10));

        await RunUntilAsync(service, () => !FakeConsumer.Of(consumer).Committed.IsEmpty);

        Assert.Equal(2, intentos);
        Assert.Single(FakeConsumer.Of(consumer).Seeks);
    }

    [Fact]
    public async Task TiempoReal_SiAvisarFalla_NoReintentaYConfirma()
    {
        var consumer = FakeConsumer.Create(JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(),
            eventType = EventTypes.ServiceRequestStatusChanged,
            eventVersion = 1,
            occurredAt = "2026-10-12T16:10:00Z",
            correlationId = Guid.NewGuid(),
            tenantId = Guid.NewGuid(),
            producer = "service-request-service",
            data = new { serviceRequestId = Guid.NewGuid(), clientId = Guid.NewGuid(), previousStatus = "en_espera", status = "buscando_tecnico", changedAt = "2026-10-12T16:10:00Z" },
        }));
        var notifier = new RealtimeNotifier(new FailingConnections(), new RecentEventIds(), NullLogger<RealtimeNotifier>.Instance);
        var service = new RealtimeEventsConsumer(consumer, notifier, NullLogger<RealtimeEventsConsumer>.Instance);

        await RunUntilAsync(service, () => !FakeConsumer.Of(consumer).Committed.IsEmpty);

        Assert.Empty(FakeConsumer.Of(consumer).Seeks);
    }

    private static async Task RunUntilAsync(Microsoft.Extensions.Hosting.BackgroundService service, Func<bool> done)
    {
        await service.StartAsync(CancellationToken.None);
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(20);
        }

        await service.StopAsync(CancellationToken.None);
        Assert.True(done());
    }

    private sealed class FailingConnections : IRealtimeConnections
    {
        public Task<int> DeliverAsync(Delivery delivery, CancellationToken cancellationToken) => throw new InvalidOperationException("canal caído");
    }
}

/// <summary>Consumidor de Kafka en memoria: entrega un mensaje, lo vuelve a entregar si se pide volver a él, y después nada.</summary>
public class FakeConsumer : DispatchProxy
{
    private readonly ConcurrentQueue<ConsumeResult<string, string>> pending = new();
    private ConsumeResult<string, string>? last;

    public ConcurrentQueue<TopicPartitionOffset> Committed { get; } = new();

    public ConcurrentQueue<TopicPartitionOffset> Seeks { get; } = new();

    public static IConsumer<string, string> Create(string value)
    {
        var consumer = DispatchProxy.Create<IConsumer<string, string>, FakeConsumer>();
        Of(consumer).pending.Enqueue(new ConsumeResult<string, string>
        {
            Message = new Message<string, string> { Key = "k", Value = value },
            TopicPartitionOffset = new TopicPartitionOffset("x", 0, 1),
        });
        return consumer;
    }

    public static FakeConsumer Of(IConsumer<string, string> consumer) => (FakeConsumer)(object)consumer;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        switch (targetMethod.Name)
        {
            case nameof(IConsumer<string, string>.Consume):
                if (pending.TryDequeue(out var item))
                {
                    last = item;
                    return item;
                }

                Thread.Sleep(5);
                return null;
            case nameof(IConsumer<string, string>.Seek) when args is [TopicPartitionOffset offset, ..]:
                Seeks.Enqueue(offset);
                pending.Enqueue(last!);
                return null;
            case nameof(IConsumer<string, string>.Commit) when args is [ConsumeResult<string, string> result, ..]:
                Committed.Enqueue(result.TopicPartitionOffset);
                return null;
            case nameof(IConsumer<string, string>.Subscribe):
            case nameof(IConsumer<string, string>.Close):
            case "Dispose":
                return null;
            default:
                throw new NotSupportedException(targetMethod.Name);
        }
    }
}