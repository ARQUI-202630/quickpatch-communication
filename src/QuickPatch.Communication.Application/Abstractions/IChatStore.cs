using QuickPatch.Communication.Domain.Chat;

namespace QuickPatch.Communication.Application.Abstractions;

/// <summary>
/// Persistencia del chat (tablas <c>chat_participants</c> y <c>chat_messages</c>, DD 5.23 y 5.24). Cada operación corre
/// en una transacción que fija el tenant (<c>app.current_tenant</c>), así que RLS solo deja ver y escribir ese tenant.
/// </summary>
public interface IChatStore
{
    /// <summary>
    /// Guarda el estado de la solicitud si es más nuevo que el que hay (RN-CH3). Devuelve true si lo aplicó.
    /// </summary>
    Task<bool> ApplyAsync(ChatParticipants participants, CancellationToken cancellationToken);

    Task<ChatParticipants?> FindAsync(Guid tenantId, Guid serviceRequestId, CancellationToken cancellationToken);

    /// <summary>Mensajes de la solicitud, del más reciente al más antiguo, y el total.</summary>
    Task<ChatMessagePage> ListAsync(Guid tenantId, Guid serviceRequestId, int skip, int take, CancellationToken cancellationToken);

    Task AddAsync(ChatMessage message, CancellationToken cancellationToken);
}

public sealed record ChatMessagePage(IReadOnlyList<ChatMessage> Items, int TotalItems);