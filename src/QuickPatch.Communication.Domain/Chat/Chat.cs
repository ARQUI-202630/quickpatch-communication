namespace QuickPatch.Communication.Domain.Chat;

/// <summary>Papel del autor en el chat (<c>communication.v1.yaml</c> 1.3.0, <c>ChatMessage.authorRole</c>).</summary>
public static class ChatRoles
{
    public const string Cliente = "cliente";
    public const string Tecnico = "tecnico";
}

/// <summary>
/// Quién participa en el chat de una solicitud y en qué estado está (tabla <c>chat_participants</c>, DD 5.23, ADR-026).
/// Es una réplica que Communication llena con <c>service-request.status-changed</c>; nunca llama a ServiceRequest.
/// </summary>
public sealed class ChatParticipants
{
    /// <summary>Estados con técnico asignado en los que se puede escribir (RN-CH2).</summary>
    public static readonly IReadOnlySet<string> OpenStatuses =
        new HashSet<string>(["asignado", "cotizado", "cotizacion_aceptada", "en_progreso"], StringComparer.Ordinal);

    /// <summary>Estados finales: el historial se lee pero no se escribe (RN-CH2).</summary>
    public static readonly IReadOnlySet<string> ClosedStatuses =
        new HashSet<string>(["completado", "pagado", "cancelado"], StringComparer.Ordinal);

    public ChatParticipants(
        Guid serviceRequestId, Guid tenantId, Guid clientId, Guid? technicianId, string status, DateTimeOffset statusChangedAt)
    {
        ServiceRequestId = serviceRequestId;
        TenantId = tenantId;
        ClientId = clientId;
        TechnicianId = technicianId;
        Status = status;
        StatusChangedAt = statusChangedAt;
    }

    public Guid ServiceRequestId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ClientId { get; private set; }

    public Guid? TechnicianId { get; private set; }

    public string Status { get; private set; }

    public DateTimeOffset StatusChangedAt { get; private set; }

    /// <summary>
    /// Papel del usuario en el chat: <c>cliente</c> si es el dueño, <c>tecnico</c> si es el técnico asignado actual, o null
    /// si no participa (RN-CH1). Un técnico anterior a una reasignación ya no participa.
    /// </summary>
    public string? RoleOf(Guid userId) =>
        userId == Guid.Empty ? null
        : userId == ClientId ? ChatRoles.Cliente
        : TechnicianId is { } technician && userId == technician ? ChatRoles.Tecnico
        : null;

    public bool IsClosed => ClosedStatuses.Contains(Status);

    /// <summary>Se puede escribir: hay técnico asignado y la solicitud está en un estado abierto (RN-CH2).</summary>
    public bool IsWritable => TechnicianId is not null && OpenStatuses.Contains(Status);
}

/// <summary>Mensaje del chat (tabla <c>chat_messages</c>, DD 5.24). El texto no se escribe en logs ni en eventos (RN-CH4).</summary>
public sealed class ChatMessage
{
    public const int MaxLength = 1000;

    public ChatMessage(Guid id, Guid tenantId, Guid serviceRequestId, Guid authorId, string authorRole, string body, DateTimeOffset sentAt)
    {
        Id = id;
        TenantId = tenantId;
        ServiceRequestId = serviceRequestId;
        AuthorId = authorId;
        AuthorRole = authorRole;
        Body = body;
        SentAt = sentAt;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ServiceRequestId { get; private set; }

    public Guid AuthorId { get; private set; }

    public string AuthorRole { get; private set; }

    public string Body { get; private set; }

    public DateTimeOffset SentAt { get; private set; }

    /// <summary>
    /// Texto normalizado: sin espacios al inicio ni al final y de 1 a 1.000 caracteres (RN-CH4). Devuelve null si no cumple.
    /// </summary>
    public static string? NormalizeText(string? text)
    {
        var clean = text?.Trim();
        return string.IsNullOrEmpty(clean) || clean.Length > MaxLength ? null : clean;
    }
}