using System.Text.Json;

using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Inbox;

namespace QuickPatch.Communication.Application.Inbox;

/// <summary>Una notificación por crear, antes de saber su id y su evento de origen.</summary>
public sealed record NewNotification(Guid UserId, string Type, string Title, string Message, Guid? ServiceRequestId);

/// <summary>
/// Decide qué notificaciones crea cada evento y para quién (tabla de tipos de <c>communication.v1.yaml</c> 1.0.0, DD 8.2.6).
/// Como el canal en tiempo real, solo usa el contenido del evento. Los textos no llevan dirección ni teléfono (RN-SR13):
/// para el detalle, la app consulta el recurso por REST.
/// </summary>
public static class InboxRules
{
    public static IReadOnlyList<NewNotification> From(EventEnvelope envelope)
    {
        EventFields.EnsureValidEnvelope(envelope);
        var data = envelope.Data;

        return envelope.EventType switch
        {
            EventTypes.ServiceRequestCreated =>
            [
                new(EventFields.Uuid(data, "clientId"), NotificationTypes.SolicitudCreada, "Recibimos tu solicitud",
                    "Estamos buscando un técnico disponible para tu solicitud.", EventFields.Uuid(data, "serviceRequestId")),
            ],

            // SCRUM-466 va a publicar la asignación desde ServiceRequest. Al cambiar de evento solo cambia esta entrada,
            // que lee la solicitud, el cliente y el técnico; tipos, destinatarios y textos están en Assignment().
            EventTypes.TechnicianAssigned => Assignment(
                EventFields.Uuid(data, "serviceRequestId"), EventFields.Uuid(data, "clientId"), EventFields.Uuid(data, "technicianId")),

            EventTypes.NoTechnicianAvailable =>
            [
                new(EventFields.Uuid(data, "clientId"), NotificationTypes.SinTecnicoDisponible, "Tu solicitud quedó en espera",
                    "Por ahora no hay técnicos disponibles. Te avisaremos cuando retomemos la búsqueda.",
                    EventFields.Uuid(data, "serviceRequestId")),
            ],
            EventTypes.SearchResumed =>
            [
                new(EventFields.Uuid(data, "clientId"), NotificationTypes.BusquedaReanudada, "Retomamos la búsqueda",
                    "Hay técnicos disponibles otra vez y estamos buscando uno para tu solicitud.",
                    EventFields.Uuid(data, "serviceRequestId")),
            ],
            EventTypes.OfferCreated =>
            [
                new(EventFields.Uuid(data, "technicianId"), NotificationTypes.OfertaRecibida, "Tienes una oferta nueva",
                    "Hay una solicitud cerca de ti. Revisa tus ofertas y respóndela antes de que venza.",
                    EventFields.Uuid(data, "serviceRequestId")),
            ],
            EventTypes.TechnicianRegistered =>
            [
                new(EventFields.Uuid(data, "technicianId"), NotificationTypes.RegistroRecibido, "Recibimos tu registro",
                    "Tu cuenta quedó pendiente de verificación. Te avisaremos cuando la revisemos.", null),
            ],
            EventTypes.VerificationChanged => [VerificationChanged(data)],
            _ => [],
        };
    }

    /// <summary>
    /// La asignación de un técnico a una solicitud: <c>tecnico-asignado</c> al cliente dueño y <c>solicitud-asignada</c>
    /// al técnico. No depende del evento del que salga.
    /// </summary>
    public static IReadOnlyList<NewNotification> Assignment(Guid serviceRequestId, Guid clientId, Guid technicianId) =>
    [
        new(clientId, NotificationTypes.TecnicoAsignado, "Ya tienes técnico",
            "Un técnico aceptó tu solicitud y va a ponerse en contacto contigo.", serviceRequestId),
        new(technicianId, NotificationTypes.SolicitudAsignada, "Tienes una solicitud asignada",
            "Revisa el detalle de la solicitud para ver la dirección y el contacto del cliente.", serviceRequestId),
    ];

    private static NewNotification VerificationChanged(JsonElement data)
    {
        var technician = EventFields.Uuid(data, "technicianId");
        var reason = EventFields.OptionalText(data, "reason");
        var (title, message) = EventFields.Text(data, "verificationStatus") switch
        {
            "aprobado" => ("Tu cuenta fue aprobada", "Ya puedes activar tu disponibilidad y recibir ofertas."),
            "rechazado" => ("Tu verificación fue rechazada", WithReason("No pudimos aprobar tu cuenta.", reason)),
            "suspendido" => ("Tu cuenta fue suspendida", WithReason("No recibirás ofertas mientras esté suspendida.", reason)),
            "pendiente" => ("Tu verificación está pendiente", "Tu cuenta volvió a quedar pendiente de verificación."),
            var other => throw new InvalidEventException($"data.verificationStatus no es válido: {other}."),
        };
        return new NewNotification(technician, NotificationTypes.VerificacionActualizada, title, message, null);
    }

    /// <summary>El motivo lo escribe el administrador; se recorta para no pasar el máximo del contrato.</summary>
    private static string WithReason(string message, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return message;
        }

        var full = $"{message} Motivo: {reason.Trim()}";
        return full.Length <= Notification.MessageMaxLength ? full : string.Concat(full.AsSpan(0, Notification.MessageMaxLength - 1), "…");
    }
}