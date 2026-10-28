using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Inbox;
using QuickPatch.Communication.UnitTests.Support;

namespace QuickPatch.Communication.UnitTests.Application;

public class InboxRecorderTests
{
    private readonly InMemoryInboxStore _store = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _client = Guid.NewGuid();
    private readonly Guid _technician = Guid.NewGuid();

    private InboxRecorder Recorder() => new(_store, NullLogger<InboxRecorder>.Instance);

    private string Assigned(Guid? eventId = null, Guid? technician = null) => Events.Json(
        EventTypes.ServiceRequestStatusChanged, _tenant, new
        {
            serviceRequestId = Guid.NewGuid(),
            clientId = _client,
            technicianId = technician ?? _technician,
            previousStatus = "buscando_tecnico",
            status = "asignado",
            changedAt = "2026-10-12T16:10:00Z",
        }, eventId);

    [Fact]
    public async Task Evento_GuardaUnaPorDestinatarioEnElTenantDelEvento()
    {
        var eventId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 12, 16, 10, 0, TimeSpan.Zero);

        Assert.Equal(RecordResult.Recorded, await Recorder().HandleAsync(Assigned(eventId), CancellationToken.None));

        Assert.Equal(2, _store.Rows.Count);
        Assert.All(_store.Rows, n =>
        {
            Assert.Equal(_tenant, n.TenantId);
            Assert.Equal(eventId, n.SourceEventId);
            Assert.Equal(at, n.CreatedAt);
            Assert.Null(n.ReadAt);
        });
    }

    [Fact]
    public async Task EventoRepetido_NoDuplica()
    {
        var eventId = Guid.NewGuid();

        await Recorder().HandleAsync(Assigned(eventId), CancellationToken.None);
        await Recorder().HandleAsync(Assigned(eventId), CancellationToken.None);

        Assert.Equal(2, _store.Rows.Count);
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("{}")]
    [InlineData("""{"eventId":"0190f3b4-1a2b-7c3d-8e4f-5a6b7c8d9e0f","eventType":"service-request.status-changed","eventVersion":1,"tenantId":"0190f3b4-1a2b-7c3d-8e4f-5a6b7c8d9e0f","occurredAt":"2026-10-12T16:10:00Z","producer":"x","data":{}}""")]
    public async Task EventoInvalido_SeDescartaSinGuardar(string message)
    {
        Assert.Equal(RecordResult.Discarded, await Recorder().HandleAsync(message, CancellationToken.None));
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task VersionDistintaDeUno_SeDescarta()
    {
        var message = Events.Json(EventTypes.ServiceRequestCreated, _tenant, new { serviceRequestId = Guid.NewGuid(), clientId = _client }, version: 2);

        Assert.Equal(RecordResult.Discarded, await Recorder().HandleAsync(message, CancellationToken.None));
    }

    [Fact]
    public async Task EventoSinNotificaciones_SeIgnora()
    {
        var message = Events.Json("catalog.category-changed", _tenant, new { categoryId = Guid.NewGuid() });

        Assert.Equal(RecordResult.Ignored, await Recorder().HandleAsync(message, CancellationToken.None));
    }

    [Fact]
    public async Task ClienteYTecnicoIguales_UnaSolaNotificacion()
    {
        await Recorder().HandleAsync(Assigned(technician: _client), CancellationToken.None);

        Assert.Equal(NotificationTypes.TecnicoAsignado, Assert.Single(_store.Rows).Type);
    }

    [Fact]
    public async Task BaseQueFalla_LaExcepcionSubeParaReintentar()
    {
        _store.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Recorder().HandleAsync(Assigned(), CancellationToken.None));
    }
}