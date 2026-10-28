using System.Text.Json;

using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.UnitTests.Application;

public class NoticeRouterTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Request = Guid.NewGuid();
    private static readonly Guid Client = Guid.NewGuid();
    private static readonly Guid Technician = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2026, 10, 12, 16, 10, 0, TimeSpan.Zero);

    internal static EventEnvelope Envelope(string type, object data, Guid? tenant = null, int version = 1) =>
        new(Guid.NewGuid(), type, version, At, Guid.NewGuid().ToString(), tenant ?? Tenant, "matching-service",
            JsonSerializer.SerializeToElement(data, RealtimeJson.Options));

    private static object Cambio(string status, Guid? technician = null) => new
    {
        serviceRequestId = Request,
        clientId = Client,
        technicianId = technician,
        previousStatus = "buscando_tecnico",
        status,
        changedAt = At,
    };

    [Fact]
    public void CambioConTecnico_AvisaAlClienteAlTecnicoYAlPanel()
    {
        var deliveries = NoticeRouter.Route(Envelope(EventTypes.ServiceRequestStatusChanged, Cambio("asignado", Technician)));

        Assert.Equal(3, deliveries.Count);
        Assert.All(deliveries, d => Assert.Equal(Tenant, d.TenantId));
        var status = Assert.IsType<ServiceRequestStatusChanged>(deliveries[0].Notice);
        Assert.Equal("asignado", status.Status);
        Assert.Equal(Request, status.ServiceRequestId);
        Assert.Equal(At, status.OccurredAt);
        Assert.Equal(new UserRecipient(Client), deliveries[0].Recipient);
        Assert.Equal(new UserRecipient(Technician), deliveries[1].Recipient);
        Assert.Equal(new RoleRecipient(Roles.AdminTenant), deliveries[2].Recipient);
        Assert.IsType<ServiceRequestCountsChanged>(deliveries[2].Notice);
    }

    [Fact]
    public void Creada_AvisaAlClienteYAlPanel()
    {
        var deliveries = NoticeRouter.Route(Envelope(EventTypes.ServiceRequestCreated, new { serviceRequestId = Request, clientId = Client }));

        Assert.Equal(2, deliveries.Count);
        Assert.Equal("buscando_tecnico", Assert.IsType<ServiceRequestStatusChanged>(deliveries[0].Notice).Status);
        Assert.Equal(new UserRecipient(Client), deliveries[0].Recipient);
        Assert.Equal(new RoleRecipient(Roles.AdminTenant), deliveries[1].Recipient);
    }

    [Theory]
    [InlineData("en_espera")]
    [InlineData("buscando_tecnico")]
    public void CambioSinTecnico_AvisaSoloAlClienteYAlPanel(string status)
    {
        var deliveries = NoticeRouter.Route(Envelope(EventTypes.ServiceRequestStatusChanged, Cambio(status)));

        Assert.Equal(2, deliveries.Count);
        Assert.Equal(status, Assert.IsType<ServiceRequestStatusChanged>(deliveries[0].Notice).Status);
        Assert.Equal(new UserRecipient(Client), deliveries[0].Recipient);
        Assert.Equal(new RoleRecipient(Roles.AdminTenant), deliveries[1].Recipient);
    }

    [Fact]
    public void CambioSinElCampoTecnico_TambienEsValido()
    {
        var deliveries = NoticeRouter.Route(Envelope(EventTypes.ServiceRequestStatusChanged,
            new { serviceRequestId = Request, clientId = Client, previousStatus = "buscando_tecnico", status = "en_espera", changedAt = At }));

        Assert.Equal(2, deliveries.Count);
    }

    [Theory]
    [InlineData("matching.technician-assigned")]
    [InlineData("matching.no-technician-available")]
    [InlineData("matching.search-resumed")]
    public void EventosDeMatching_YaNoGeneranAvisoDeEstado(string type)
    {
        // SCRUM-466: ServiceRequest puede descartar estos eventos por viejos; el aviso sale de su status-changed.
        Assert.DoesNotContain(type, EventTypes.Consumed);
        Assert.Empty(NoticeRouter.Route(Envelope(type,
            new { serviceRequestId = Request, clientId = Client, attemptId = Guid.NewGuid(), technicianId = Technician, assignedAt = At })));
    }

    [Fact]
    public void OfferCreated_SoloAvisaAlTecnicoSinDatosDelCliente()
    {
        var attempt = Guid.NewGuid();
        var expires = At.AddMinutes(3);
        var deliveries = NoticeRouter.Route(Envelope(EventTypes.OfferCreated,
            new { serviceRequestId = Request, attemptId = attempt, technicianId = Technician, expiresAt = expires }));

        var delivery = Assert.Single(deliveries);
        Assert.Equal(new UserRecipient(Technician), delivery.Recipient);
        Assert.Equal(new OfferCreated(attempt, Request, expires, At), delivery.Notice);
    }

    [Fact]
    public void EventoQueNoConsume_NoGeneraAvisos() =>
        Assert.Empty(NoticeRouter.Route(Envelope("catalog.category-changed", new { categoryId = Guid.NewGuid() })));

    [Fact]
    public void ClienteYTecnicoIguales_NoSeAvisaDosVeces()
    {
        var deliveries = NoticeRouter.Route(Envelope(EventTypes.ServiceRequestStatusChanged, Cambio("asignado", Client)));

        Assert.Equal(2, deliveries.Count);
    }

    [Theory]
    [InlineData("{\"serviceRequestId\":\"no-es-uuid\",\"clientId\":\"00000000-0000-0000-0000-000000000001\",\"status\":\"en_espera\"}")]
    [InlineData("{\"serviceRequestId\":\"00000000-0000-0000-0000-000000000001\",\"status\":\"en_espera\"}")]
    [InlineData("{\"serviceRequestId\":\"00000000-0000-0000-0000-000000000000\",\"clientId\":\"00000000-0000-0000-0000-000000000001\",\"status\":\"en_espera\"}")]
    [InlineData("{\"serviceRequestId\":\"00000000-0000-0000-0000-000000000001\",\"clientId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"serviceRequestId\":\"00000000-0000-0000-0000-000000000001\",\"clientId\":\"00000000-0000-0000-0000-000000000001\",\"status\":\"\"}")]
    [InlineData("{\"serviceRequestId\":\"00000000-0000-0000-0000-000000000001\",\"clientId\":\"00000000-0000-0000-0000-000000000001\",\"status\":\"asignado\",\"technicianId\":\"no-es-uuid\"}")]
    public void DatosInvalidos_LanzanInvalidEvent(string data)
    {
        var envelope = new EventEnvelope(Guid.NewGuid(), EventTypes.ServiceRequestStatusChanged, 1, At, null, Tenant, "service-request-service",
            JsonDocument.Parse(data).RootElement.Clone());

        Assert.Throws<InvalidEventException>(() => NoticeRouter.Route(envelope));
    }

    [Fact]
    public void FechaDeVencimientoInvalida_LanzaInvalidEvent() =>
        Assert.Throws<InvalidEventException>(() => NoticeRouter.Route(Envelope(EventTypes.OfferCreated,
            new { serviceRequestId = Request, attemptId = Guid.NewGuid(), technicianId = Technician, expiresAt = "mañana" })));

    [Fact]
    public void SobreSinTenantOConVersionDesconocida_LanzaInvalidEvent()
    {
        Assert.Throws<InvalidEventException>(() => NoticeRouter.Route(Envelope(EventTypes.ServiceRequestStatusChanged, Cambio("en_espera"), Guid.Empty)));
        Assert.Throws<InvalidEventException>(() => NoticeRouter.Route(Envelope(EventTypes.ServiceRequestStatusChanged, Cambio("en_espera"), version: 2)));
    }

    [Fact]
    public void RolesDelCanal()
    {
        Assert.True(Roles.UsesRealtimeChannel(Roles.Cliente));
        Assert.True(Roles.UsesRealtimeChannel(Roles.EmpresaContacto));
        Assert.True(Roles.UsesRealtimeChannel(Roles.Tecnico));
        Assert.True(Roles.UsesRealtimeChannel(Roles.AdminTenant));
        Assert.False(Roles.UsesRealtimeChannel("admin_plataforma"));
        Assert.False(Roles.UsesRealtimeChannel("proveedor"));
    }
}