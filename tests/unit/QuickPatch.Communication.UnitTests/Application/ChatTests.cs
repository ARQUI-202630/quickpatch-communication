using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Chat;
using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Chat;
using QuickPatch.Communication.Domain.Realtime;
using QuickPatch.Communication.UnitTests.Support;

namespace QuickPatch.Communication.UnitTests.Application;

/// <summary>Chat de la solicitud (RF-37, ADR-026): réplica de participantes, reglas RN-CH1 a RN-CH4 y entrega por el canal.</summary>
public class ChatTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);

    private readonly InMemoryChatStore _store = new();
    private readonly FakeConnections _connections = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _client = Guid.NewGuid();
    private readonly Guid _technician = Guid.NewGuid();
    private readonly Guid _request = Guid.NewGuid();

    private sealed class FakeConnections : IRealtimeConnections
    {
        public List<Delivery> Deliveries { get; } = [];

        public bool Fail { get; set; }

        public Task<int> DeliverAsync(Delivery delivery, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("socket cerrado");
            }

            Deliveries.Add(delivery);
            return Task.FromResult(1);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private string StatusChanged(string status, Guid? technician, int minutes, string type = EventTypes.ServiceRequestStatusChanged, Guid? tenant = null) =>
        JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(),
            eventType = type,
            eventVersion = 1,
            occurredAt = T0.AddMinutes(minutes),
            correlationId = Guid.NewGuid(),
            tenantId = tenant ?? _tenant,
            producer = "service-request-service",
            data = new
            {
                serviceRequestId = _request,
                clientId = _client,
                technicianId = technician,
                previousStatus = "buscando_tecnico",
                status,
                changedAt = T0.AddMinutes(minutes),
            },
        });

    private ChatParticipantsRecorder Recorder() => new(_store, NullLogger<ChatParticipantsRecorder>.Instance);

    private SendChatMessageHandler Sender(int minutes = 30) =>
        new(_store, _connections, new FixedClock(T0.AddMinutes(minutes)), NullLogger<SendChatMessageHandler>.Instance);

    private Task<RecordResult> Seed(string status, Guid? technician, int minutes = 1) => Recorder().HandleAsync(StatusChanged(status, technician, minutes), CancellationToken.None);

    [Fact]
    public async Task Replica_AplicaSoloEstadosMasNuevos_EIgnoraOtrosEventos()
    {
        var recorder = Recorder();

        Assert.Equal(RecordResult.Recorded, await recorder.HandleAsync(StatusChanged("asignado", _technician, 1), CancellationToken.None));
        Assert.Equal(RecordResult.Recorded, await recorder.HandleAsync(StatusChanged("en_progreso", _technician, 5), CancellationToken.None));
        Assert.Equal(RecordResult.Ignored, await recorder.HandleAsync(StatusChanged("cotizado", _technician, 3), CancellationToken.None));
        Assert.Equal(RecordResult.Ignored, await recorder.HandleAsync(StatusChanged("asignado", _technician, 9, "service-request.created"), CancellationToken.None));

        var p = _store.Participants[_request];
        Assert.Equal(("en_progreso", _technician, _tenant), (p.Status, p.TechnicianId, p.TenantId));
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("null")]
    [InlineData("""{"eventId":"00000000-0000-0000-0000-000000000000","eventType":"service-request.status-changed"}""")]
    [InlineData("""{"eventId":"7b1d5f3e-0000-4000-8000-000000000001","eventType":"service-request.status-changed","eventVersion":1,"occurredAt":"2026-10-21T15:00:00Z","tenantId":"7b1d5f3e-0000-4000-8000-000000000002","producer":"x","data":{"serviceRequestId":"7b1d5f3e-0000-4000-8000-000000000003","status":"asignado","changedAt":"2026-10-21T15:00:00Z"}}""")]
    public async Task Replica_EventoInvalido_SeDescarta(string message)
    {
        Assert.Equal(RecordResult.Discarded, await Recorder().HandleAsync(message, CancellationToken.None));
        Assert.Empty(_store.Participants);
    }

    [Fact]
    public void Participantes_SoloElDuenoYElTecnicoActual()
    {
        var p = new ChatParticipants(_request, _tenant, _client, _technician, "asignado", T0);

        Assert.Equal(ChatRoles.Cliente, p.RoleOf(_client));
        Assert.Equal(ChatRoles.Tecnico, p.RoleOf(_technician));
        Assert.Null(p.RoleOf(Guid.NewGuid()));
        Assert.Null(p.RoleOf(Guid.Empty));
        Assert.True(p.IsWritable);
        Assert.Null(new ChatParticipants(_request, _tenant, _client, null, "buscando_tecnico", T0).RoleOf(_technician));
    }

    [Theory]
    [InlineData("asignado", true, false)]
    [InlineData("cotizado", true, false)]
    [InlineData("cotizacion_aceptada", true, false)]
    [InlineData("en_progreso", true, false)]
    [InlineData("completado", false, true)]
    [InlineData("pagado", false, true)]
    [InlineData("cancelado", false, true)]
    [InlineData("en_espera", false, false)]
    public void Estados_EscrituraYCierre(string status, bool writable, bool closed)
    {
        var p = new ChatParticipants(_request, _tenant, _client, status == "en_espera" ? null : _technician, status, T0);

        Assert.Equal((writable, closed), (p.IsWritable, p.IsClosed));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  hola  ", "hola")]
    public void Texto_SeNormaliza(string? text, string? expected) => Assert.Equal(expected, ChatMessage.NormalizeText(text));

    [Fact]
    public void Texto_DeMasDeMilCaracteres_NoSeAcepta()
    {
        Assert.NotNull(ChatMessage.NormalizeText(new string('a', ChatMessage.MaxLength)));
        Assert.Null(ChatMessage.NormalizeText(new string('a', ChatMessage.MaxLength + 1)));
    }

    [Fact]
    public async Task Enviar_GuardaYEntregaALosDosParticipantes()
    {
        await Seed("asignado", _technician);

        var result = Assert.IsType<ChatResult.Sent>(
            await Sender().HandleAsync(_tenant, _technician, _request, "  Llego a las 3 p. m.  ", CancellationToken.None));

        var message = Assert.Single(_store.Messages);
        Assert.Equal(("Llego a las 3 p. m.", ChatRoles.Tecnico, _technician, T0.AddMinutes(30)), (message.Body, message.AuthorRole, message.AuthorId, message.SentAt));
        Assert.Equal(message, result.Message);
        Assert.Equal([_client, _technician], _connections.Deliveries.Select(d => ((UserRecipient)d.Recipient).UserId));
        Assert.All(_connections.Deliveries, d => Assert.Equal(_tenant, d.TenantId));
        var notice = Assert.IsType<ChatMessageNotice>(_connections.Deliveries[0].Notice);
        Assert.Equal(("chat.message", _request, message.Id, "Llego a las 3 p. m."), (notice.Type, notice.ServiceRequestId, notice.Message.Id, notice.Message.Text));
    }

    [Fact]
    public async Task Enviar_SiElCanalFalla_ElMensajeQuedaGuardado()
    {
        await Seed("asignado", _technician);
        _connections.Fail = true;

        Assert.IsType<ChatResult.Sent>(await Sender().HandleAsync(_tenant, _client, _request, "hola", CancellationToken.None));
        Assert.Single(_store.Messages);
    }

    [Fact]
    public async Task Enviar_NoParticipante_OtroTenant_ODesconocida_404()
    {
        await Seed("asignado", _technician);
        var sender = Sender();

        Assert.IsType<ChatResult.NotFound>(await sender.HandleAsync(_tenant, Guid.NewGuid(), _request, "hola", CancellationToken.None));
        Assert.IsType<ChatResult.NotFound>(await sender.HandleAsync(Guid.NewGuid(), _client, _request, "hola", CancellationToken.None));
        Assert.IsType<ChatResult.NotFound>(await sender.HandleAsync(_tenant, _client, Guid.NewGuid(), "hola", CancellationToken.None));
        Assert.Empty(_store.Messages);
    }

    [Fact]
    public async Task Enviar_Cerrado_SinTecnico_YReasignado()
    {
        await Seed("completado", _technician);
        Assert.IsType<ChatResult.Closed>(await Sender().HandleAsync(_tenant, _client, _request, "gracias", CancellationToken.None));

        await Seed("buscando_tecnico", null, minutes: 40);
        Assert.IsType<ChatResult.NoTechnician>(await Sender().HandleAsync(_tenant, _client, _request, "hola", CancellationToken.None));

        // El técnico anterior ya no participa.
        Assert.IsType<ChatResult.NotFound>(await Sender().HandleAsync(_tenant, _technician, _request, "hola", CancellationToken.None));
        Assert.Empty(_store.Messages);
    }

    [Fact]
    public async Task Enviar_TextoVacio_ErrorEnText()
    {
        await Seed("asignado", _technician);

        var ex = await Assert.ThrowsAsync<ChatValidationException>(() => Sender().HandleAsync(_tenant, _client, _request, " ", CancellationToken.None));

        Assert.True(ex.Errors.ContainsKey("text"));
    }

    [Fact]
    public async Task Listar_HistorialPaginadoYWritable()
    {
        await Seed("asignado", _technician);
        for (var i = 0; i < 3; i++)
        {
            await Sender(minutes: 10 + i).HandleAsync(_tenant, i % 2 == 0 ? _client : _technician, _request, $"m{i}", CancellationToken.None);
        }

        var handler = new ListChatMessagesHandler(_store);
        var first = Assert.IsType<ChatResult.History>(await handler.HandleAsync(_tenant, _client, _request, 1, 2, CancellationToken.None));
        Assert.Equal(["m2", "m1"], first.Items.Select(m => m.Body));
        Assert.Equal((3, true), (first.TotalItems, first.Writable));

        var second = Assert.IsType<ChatResult.History>(await handler.HandleAsync(_tenant, _technician, _request, 2, 2, CancellationToken.None));
        Assert.Equal(["m0"], second.Items.Select(m => m.Body));

        await Seed("pagado", _technician, minutes: 50);
        var closed = Assert.IsType<ChatResult.History>(await handler.HandleAsync(_tenant, _client, _request, null, null, CancellationToken.None));
        Assert.Equal((3, false, 1, ListChatMessagesHandler.DefaultSize), (closed.TotalItems, closed.Writable, closed.Page, closed.Size));

        Assert.IsType<ChatResult.NotFound>(await handler.HandleAsync(_tenant, Guid.NewGuid(), _request, null, null, CancellationToken.None));
        Assert.IsType<ChatResult.NotFound>(await handler.HandleAsync(Guid.NewGuid(), _client, _request, null, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 10, "page")]
    [InlineData(1, 0, "size")]
    [InlineData(1, 101, "size")]
    public async Task Listar_PaginaInvalida(int page, int size, string field)
    {
        var ex = await Assert.ThrowsAsync<ChatValidationException>(
            () => new ListChatMessagesHandler(_store).HandleAsync(_tenant, _client, _request, page, size, CancellationToken.None));

        Assert.True(ex.Errors.ContainsKey(field));
    }

    [Fact]
    public async Task Replica_SiLaBaseFalla_LaExcepcionSube()
    {
        _store.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Recorder().HandleAsync(StatusChanged("asignado", _technician, 1), CancellationToken.None));
    }
}