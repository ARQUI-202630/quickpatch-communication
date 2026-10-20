using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Inbox;

namespace QuickPatch.Communication.UnitTests.Inbox;

/// <summary>Tabla de tipos de <c>communication.v1.yaml</c> 1.2.0 (DD 8.2.6): qué crea cada evento y para quién.</summary>
public class InboxRulesTests
{
    private static readonly Guid Request = Guid.NewGuid();
    private static readonly Guid Client = Guid.NewGuid();
    private static readonly Guid Technician = Guid.NewGuid();

    /// <summary><c>data</c> de <c>service-request.status-changed</c> v1 (SCRUM-466).</summary>
    private static object Changed(string previous, string status, Guid? technician = null) => new
    {
        serviceRequestId = Request,
        clientId = Client,
        technicianId = technician,
        previousStatus = previous,
        status,
        changedAt = "2026-10-19T16:10:00Z",
    };

    public static TheoryData<string, object, string, string> ClientOrTechnicianEvents() => new()
    {
        { EventTypes.ServiceRequestCreated, new { serviceRequestId = Request, clientId = Client }, NotificationTypes.SolicitudCreada, "cliente" },
        { EventTypes.ServiceRequestStatusChanged, Changed("buscando_tecnico", "en_espera"), NotificationTypes.SinTecnicoDisponible, "cliente" },
        { EventTypes.ServiceRequestStatusChanged, Changed("en_espera", "buscando_tecnico"), NotificationTypes.BusquedaReanudada, "cliente" },
        { EventTypes.ServiceRequestStatusChanged, Changed("buscando_tecnico", "cancelado"), NotificationTypes.SolicitudCancelada, "cliente" },
        { EventTypes.OfferCreated, new { serviceRequestId = Request, technicianId = Technician, attemptId = Guid.NewGuid() }, NotificationTypes.OfertaRecibida, "tecnico" },
        { EventTypes.Quoted, Quoted(), NotificationTypes.CotizacionRecibida, "cliente" },
        { EventTypes.QuoteAccepted, new { serviceRequestId = Request, quoteId = Guid.NewGuid(), technicianId = Technician, totalAmount = "185000.00" }, NotificationTypes.CotizacionAceptada, "tecnico" },
        { EventTypes.QuoteRejected, Rejected(2), NotificationTypes.CotizacionRechazada, "tecnico" },
    };

    /// <summary><c>data</c> de <c>service-request.quoted</c> v1 (quickpatch-kafka v0.6.0).</summary>
    private static object Quoted() => new
    {
        serviceRequestId = Request,
        quoteId = Guid.NewGuid(),
        clientId = Client,
        technicianId = Technician,
        quoteNumber = 1,
        totalAmount = "185000.00",
        expiresAt = "2026-10-20T20:30:00Z",
    };

    /// <summary><c>data</c> de <c>service-request.quote-rejected</c> v1.</summary>
    private static object Rejected(int remaining) => new
    {
        serviceRequestId = Request,
        quoteId = Guid.NewGuid(),
        technicianId = Technician,
        remainingQuotes = remaining,
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
    public void Asignado_AvisaAlClienteYAlTecnico()
    {
        var result = InboxRules.From(Events.Envelope(
            EventTypes.ServiceRequestStatusChanged, Changed("buscando_tecnico", "asignado", Technician)));

        Assert.Equal(2, result.Count);
        Assert.Contains(result, n => n.UserId == Client && n.Type == NotificationTypes.TecnicoAsignado && n.ServiceRequestId == Request);
        Assert.Contains(result, n => n.UserId == Technician && n.Type == NotificationTypes.SolicitudAsignada && n.ServiceRequestId == Request);
    }

    [Theory]
    [InlineData("asignado", "cotizado")]
    [InlineData("cotizado", "cotizacion_aceptada")]
    [InlineData("cotizado", "asignado")]
    [InlineData("cotizacion_aceptada", "en_progreso")]
    [InlineData("buscando_tecnico", "buscando_tecnico")]
    public void OtroCambioDeEstado_SinNotificacionDelBuzon(string previous, string status)
    {
        // Sin tipo de estado en communication.v1.yaml 1.2.0: el aviso solo va por el canal en tiempo real. El regreso de
        // cotizado a asignado no repite la asignación: lo avisa cotizacion-rechazada.
        Assert.Empty(InboxRules.From(Events.Envelope(EventTypes.ServiceRequestStatusChanged, Changed(previous, status, Technician))));
    }

    [Fact]
    public void Cancelado_AvisaAlClienteYAlTecnicoAsignadoSinElMotivo()
    {
        var result = InboxRules.From(Events.Envelope(EventTypes.ServiceRequestStatusChanged, Changed("asignado", "cancelado", Technician)));

        Assert.Equal(2, result.Count);
        Assert.All(result, n =>
        {
            Assert.Equal(NotificationTypes.SolicitudCancelada, n.Type);
            Assert.Equal(Request, n.ServiceRequestId);
            Assert.DoesNotContain("Motivo", n.Message, StringComparison.Ordinal);
        });
        Assert.Contains(result, n => n.UserId == Client);
        Assert.Contains(result, n => n.UserId == Technician);
    }

    [Fact]
    public void CanceladoSinTecnico_SoloAlCliente()
    {
        var n = Assert.Single(InboxRules.From(Events.Envelope(EventTypes.ServiceRequestStatusChanged, Changed("buscando_tecnico", "cancelado"))));

        Assert.Equal((Client, NotificationTypes.SolicitudCancelada), (n.UserId, n.Type));
    }

    [Fact]
    public void AsignadoSinTecnico_EventoInvalido()
    {
        Assert.Throws<InvalidEventException>(() =>
            InboxRules.From(Events.Envelope(EventTypes.ServiceRequestStatusChanged, Changed("buscando_tecnico", "asignado"))));
    }

    [Fact]
    public void EventosDeMatchingDeEstado_YaNoCreanNotificaciones()
    {
        // Desde SCRUM-466 el estado sale de ServiceRequest: matching.technician-assigned no está en EventTypes.Inbox.
        Assert.Empty(InboxRules.From(Events.Envelope("matching.technician-assigned", new { serviceRequestId = Request, clientId = Client, technicianId = Technician })));
        Assert.DoesNotContain("matching.technician-assigned", EventTypes.Inbox);
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
    public void Cotizacion_AvisaAlClienteConElTotalEnPesosYElVencimientoEnHoraDeBogota()
    {
        var n = Assert.Single(InboxRules.From(Events.Envelope(EventTypes.Quoted, Quoted())));

        Assert.Contains("$185.000", n.Message, StringComparison.Ordinal);
        Assert.Contains("20 de octubre a las 3:30", n.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, "te quedan 2")]
    [InlineData(1, "es la última")]
    [InlineData(0, "la solicitud se canceló")]
    public void Rechazo_LeDiceAlTecnicoCuantasLeQuedan(int remaining, string expected)
    {
        var n = Assert.Single(InboxRules.From(Events.Envelope(EventTypes.QuoteRejected, Rejected(remaining))));

        Assert.Equal(Technician, n.UserId);
        Assert.Contains(expected, n.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CotizacionConMontoInvalido_EsInvalida()
    {
        Assert.Throws<InvalidEventException>(() => InboxRules.From(Events.Envelope(EventTypes.QuoteAccepted, new
        {
            serviceRequestId = Request,
            quoteId = Guid.NewGuid(),
            technicianId = Technician,
            totalAmount = "ciento ochenta",
        })));
    }

    [Fact]
    public void EventoQueNoEstaEnLaTabla_NoCreaNada()
    {
        Assert.Empty(InboxRules.From(Events.Envelope("catalog.category-changed", new { categoryId = Guid.NewGuid() })));
    }

    [Theory]
    [InlineData("service-request.status-changed")]
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