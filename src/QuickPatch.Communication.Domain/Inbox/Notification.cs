namespace QuickPatch.Communication.Domain.Inbox;

/// <summary>Tipos de notificación del buzón (<c>communication.v1.yaml</c> 1.2.0, <c>NotificationType</c>).</summary>
public static class NotificationTypes
{
    public const string SolicitudCreada = "solicitud-creada";
    public const string TecnicoAsignado = "tecnico-asignado";
    public const string SolicitudAsignada = "solicitud-asignada";
    public const string SinTecnicoDisponible = "sin-tecnico-disponible";
    public const string BusquedaReanudada = "busqueda-reanudada";
    public const string OfertaRecibida = "oferta-recibida";
    public const string RegistroRecibido = "registro-recibido";
    public const string VerificacionActualizada = "verificacion-actualizada";

    /// <summary>Desde 1.1.0 (RF-36, SCRUM-472).</summary>
    public const string SolicitudCancelada = "solicitud-cancelada";

    /// <summary>Desde 1.2.0 (RF-34, RF-35, SCRUM-488): salen de los eventos de la cotización.</summary>
    public const string CotizacionRecibida = "cotizacion-recibida";

    public const string CotizacionAceptada = "cotizacion-aceptada";

    public const string CotizacionRechazada = "cotizacion-rechazada";

    public static IReadOnlyList<string> All { get; } =
    [
        SolicitudCreada, TecnicoAsignado, SolicitudAsignada, SinTecnicoDisponible, BusquedaReanudada, OfertaRecibida,
        RegistroRecibido, VerificacionActualizada, SolicitudCancelada, CotizacionRecibida, CotizacionAceptada, CotizacionRechazada,
    ];
}

/// <summary>
/// Notificación del buzón (DD 5.22, ADR-024). La crea Communication al consumir un evento, una por destinatario; la app
/// solo la lista y la marca como leída. El texto no lleva dirección ni teléfono (RN-SR13).
/// </summary>
public sealed class Notification
{
    public const int TitleMaxLength = 100;
    public const int MessageMaxLength = 500;

    public Notification(
        Guid id,
        Guid tenantId,
        Guid userId,
        string type,
        string title,
        string message,
        Guid? serviceRequestId,
        Guid sourceEventId,
        DateTimeOffset createdAt,
        DateTimeOffset? readAt = null)
    {
        Id = id;
        TenantId = tenantId;
        UserId = userId;
        Type = type;
        Title = title;
        Message = message;
        ServiceRequestId = serviceRequestId;
        SourceEventId = sourceEventId;
        CreatedAt = createdAt;
        ReadAt = readAt;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public Guid UserId { get; }

    public string Type { get; }

    public string Title { get; }

    public string Message { get; }

    public Guid? ServiceRequestId { get; }

    /// <summary><c>eventId</c> del evento que la originó; con <see cref="UserId"/> es único (un evento repetido no la duplica).</summary>
    public Guid SourceEventId { get; }

    /// <summary><c>occurredAt</c> del evento.</summary>
    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? ReadAt { get; private set; }

    /// <summary>Idempotente: si ya estaba leída conserva el <c>readAt</c> original (contrato 1.0.0).</summary>
    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;
}