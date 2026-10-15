using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Inbox;

namespace QuickPatch.Communication.UnitTests.Inbox;

/// <summary>Tabla de tipos de <c>communication.v1.yaml</c> 1.0.0 (DD 8.2.6): qué crea cada evento y para quién.</summary>
public class InboxRulesTests
{
    private static readonly Guid Request = Guid.NewGuid();
    private static readonly Guid Client = Guid.NewGuid();
    private static readonly Guid Technician = Guid.NewGuid();

    public static TheoryData<string, object, string, string> ClientOrTechnicianEvents() => new()
    {
        { EventTypes.ServiceRequestCreated, new { serviceRequestId = Request, clientId = Client }, NotificationTypes.SolicitudCreada, "cliente" },
        { EventTypes.NoTechnicianAvailable, new { serviceRequestId = Request, clientId = Client }, NotificationTypes.SinTecnicoDisponible, "cliente" },
        { EventTypes.SearchResumed, new { serviceRequestId = Request, clientId = Client }, NotificationTypes.BusquedaReanudada, "cliente" },
        { EventTypes.OfferCreated, new { serviceRequestId = Request, technicianId = Technician, attemptId = Guid.NewGuid() }, NotificationTypes.OfertaRecibida, "tecnico" },
    };

    [Theory]
    [MemberData(nameof(ClientOrTechnicianEvents))]
    public void EventoDeSolicitud_UnaNotificacionAlDestinatarioConLaSolicitud(string eventType, object data, string type, string recipient)
    {
        var n = Assert.Single(InboxRules.From(Events.Envelope(eventType, data)));

        Assert.Equal(type, n.Type);
        Assert.Equal(recipient == "cliente" ? Client : Technician, n.UserId);
        Assert.Equal(Request, n.ServiceRequestId);
        Assert.InRange(n.Title.Length, 1, Notification.TitleMaxLength);
        Assert.InRange(n.Message.Length, 1, Notification.MessageMaxLength);
    }

    [Fact]
    public void TecnicoAsignado_AvisaAlClienteYAlTecnico()
    {
        var result = InboxRules.From(Events.Envelope(EventTypes.TechnicianAssigned, new
        {
            serviceRequestId = Request,
            clientId = Client,
            attemptId = Guid.NewGuid(),
            technicianId = Technician,
            assignedAt = "2026-10-12T16:10:00Z",
        }));

        Assert.Equal(2, result.Count);
        Assert.Contains(result, n => n.UserId == Client && n.Type == NotificationTypes.TecnicoAsignado && n.ServiceRequestId == Request);
        Assert.Contains(result, n => n.UserId == Technician && n.Type == NotificationTypes.SolicitudAsignada && n.ServiceRequestId == Request);
    }

    [Fact]
    public void Asignacion_NoDependeDelEvento()
    {
        // SCRUM-466 cambiará el evento de origen; la regla de la asignación es la misma.
        Assert.Equal(
            InboxRules.Assignment(Request, Client, Technician),
            InboxRules.From(Events.Envelope(EventTypes.TechnicianAssigned, new { serviceRequestId = Request, clientId = Client, technicianId = Technician })));
    }

    [Fact]
    public void TecnicoRegistrado_RegistroRecibidoSinSolicitud()
    {
        var n = Assert.Single(InboxRules.From(Events.Envelope(EventTypes.TechnicianRegistered, new
        {
            technicianId = Technician,
            specialtyId = Guid.NewGuid(),
            providerId = (Guid?)null,
            verificationStatus = "pendiente",
            updatedAt = "2026-10-12T16:10:00Z",
        })));

        Assert.Equal(NotificationTypes.RegistroRecibido, n.Type);
        Assert.Equal(Technician, n.UserId);
        Assert.Null(n.ServiceRequestId);
    }

    [Theory]
    [InlineData("aprobado", null, "aprobada")]
    [InlineData("rechazado", "Falta el documento.", "Motivo: Falta el documento.")]
    [InlineData("suspendido", null, "suspendida")]
    [InlineData("pendiente", null, "pendiente")]
    public void VerificacionCambiada_TextoSegunElEstado(string status, string? reason, string expected)
    {
        var n = Assert.Single(InboxRules.From(Events.Envelope(EventTypes.VerificationChanged, new
        {
            technicianId = Technician,
            specialtyId = Guid.NewGuid(),
            previousStatus = "pendiente",
            verificationStatus = status,
            reason,
            updatedAt = "2026-10-12T16:10:00Z",
        })));

        Assert.Equal(NotificationTypes.VerificacionActualizada, n.Type);
        Assert.Equal(Technician, n.UserId);
        Assert.Null(n.ServiceRequestId);
        Assert.Contains(expected, n.Title + " " + n.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificacionConMotivoLargo_SeRecortaAlMaximoDelContrato()
    {
        var n = Assert.Single(InboxRules.From(Events.Envelope(EventTypes.VerificationChanged, new
        {
            technicianId = Technician,
            verificationStatus = "rechazado",
            reason = new string('x', 2000),
        })));

        Assert.Equal(Notification.MessageMaxLength, n.Message.Length);
        Assert.EndsWith("…", n.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EventoQueNoEstaEnLaTabla_NoCreaNada()
    {
        Assert.Empty(InboxRules.From(Events.Envelope("catalog.category-changed", new { categoryId = Guid.NewGuid() })));
    }

    [Theory]
    [InlineData("matching.technician-assigned")]
    [InlineData("technician.verification-changed")]
    public void EventoSinLosCamposDelContrato_EsInvalido(string eventType)
    {
        Assert.Throws<InvalidEventException>(() => InboxRules.From(Events.Envelope(eventType, new { serviceRequestId = "no-es-uuid", verificationStatus = "otro" })));
    }

    [Fact]
    public void EstadoDeVerificacionDesconocido_EsInvalido()
    {
        Assert.Throws<InvalidEventException>(() => InboxRules.From(Events.Envelope(EventTypes.VerificationChanged, new
        {
            technicianId = Technician,
            verificationStatus = "otro",
        })));
    }

    [Fact]
    public void TodosLosTiposDelContrato_TienenRegla()
    {
        var produced = new HashSet<string>();
        foreach (var row in ClientOrTechnicianEvents())
        {
            produced.UnionWith(InboxRules.From(Events.Envelope((string)row[0], row[1])).Select(n => n.Type));
        }

        produced.UnionWith(InboxRules.Assignment(Request, Client, Technician).Select(n => n.Type));
        produced.Add(NotificationTypes.RegistroRecibido);
        produced.Add(NotificationTypes.VerificacionActualizada);
        Assert.Equal(NotificationTypes.All.Order(StringComparer.Ordinal), produced.Order(StringComparer.Ordinal));
    }
}