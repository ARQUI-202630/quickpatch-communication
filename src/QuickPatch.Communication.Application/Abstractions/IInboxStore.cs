using QuickPatch.Communication.Domain.Inbox;

namespace QuickPatch.Communication.Application.Abstractions;

/// <summary>
/// Persistencia del buzón (tabla <c>notifications</c>, DD 5.22). Cada operación corre en una transacción que fija el tenant
/// (<c>app.current_tenant</c>), así que RLS solo deja ver y escribir filas de ese tenant.
/// </summary>
public interface IInboxStore
{
    /// <summary>
    /// Guarda las notificaciones de un evento, todas del mismo tenant. Las que ya existen para el mismo evento y usuario se
    /// omiten (un evento repetido no las duplica). Devuelve cuántas se crearon.
    /// </summary>
    Task<int> AddAsync(Guid tenantId, IReadOnlyList<Notification> notifications, CancellationToken cancellationToken);

    /// <summary>Página del buzón del usuario, de la más reciente a la más antigua, con el total del filtro y las no leídas.</summary>
    Task<NotificationPage> ListAsync(Guid tenantId, Guid userId, bool unreadOnly, int skip, int take, CancellationToken cancellationToken);

    /// <summary>Marca como leída la notificación del usuario; null si no existe o es de otro usuario o tenant.</summary>
    Task<Notification?> MarkReadAsync(Guid tenantId, Guid userId, Guid id, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed record NotificationPage(IReadOnlyList<Notification> Items, int TotalItems, int UnreadCount);