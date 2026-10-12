namespace QuickPatch.Communication.Domain.Realtime;

/// <summary>
/// Aviso del canal en tiempo real (ADR-023; contrato <c>asyncapi/realtime.v1.yaml</c>). Solo avisa que algo cambió:
/// nunca lleva el detalle, que la app pide por REST (RN-SR13).
/// </summary>
public abstract record RealtimeNotice(string Type, DateTimeOffset OccurredAt);

/// <summary><c>service-request.status-changed</c>: cambió el estado de una solicitud.</summary>
public sealed record ServiceRequestStatusChanged(Guid ServiceRequestId, string Status, DateTimeOffset OccurredAt)
    : RealtimeNotice(TypeName, OccurredAt)
{
    public const string TypeName = "service-request.status-changed";
}

/// <summary><c>offer.created</c>: el técnico tiene una oferta nueva.</summary>
public sealed record OfferCreated(Guid AttemptId, Guid ServiceRequestId, DateTimeOffset ExpiresAt, DateTimeOffset OccurredAt)
    : RealtimeNotice(TypeName, OccurredAt)
{
    public const string TypeName = "offer.created";
}

/// <summary><c>service-request.counts-changed</c>: cambiaron los conteos del panel del <c>admin_tenant</c>.</summary>
public sealed record ServiceRequestCountsChanged(DateTimeOffset OccurredAt) : RealtimeNotice(TypeName, OccurredAt)
{
    public const string TypeName = "service-request.counts-changed";
}

/// <summary>A quién se entrega un aviso dentro del tenant del evento.</summary>
public abstract record Recipient;

/// <summary>Un usuario concreto (claim <c>sub</c>).</summary>
public sealed record UserRecipient(Guid UserId) : Recipient;

/// <summary>Todos los usuarios conectados con un rol (por ejemplo, los <c>admin_tenant</c>).</summary>
public sealed record RoleRecipient(string Role) : Recipient;

/// <summary>Un aviso para un destinatario. El tenant sale del evento de Kafka, nunca de la app.</summary>
public sealed record Delivery(Guid TenantId, Recipient Recipient, RealtimeNotice Notice);

/// <summary>Roles de <c>users.role</c> (DD 5.2) y cuáles usan el canal.</summary>
public static class Roles
{
    public const string Cliente = "cliente";
    public const string EmpresaContacto = "empresa_contacto";
    public const string Tecnico = "tecnico";
    public const string AdminTenant = "admin_tenant";

    private static readonly HashSet<string> Channel = [Cliente, EmpresaContacto, Tecnico, AdminTenant];

    /// <summary><c>admin_plataforma</c> y <c>proveedor</c> no usan el canal (cierre 4003).</summary>
    public static bool UsesRealtimeChannel(string role) => Channel.Contains(role);
}