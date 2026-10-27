using System.Collections.Concurrent;
using System.Text.Json;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.Api.Realtime;

/// <summary>Una conexión autenticada del canal.</summary>
public interface IRealtimeClient
{
    Guid ConnectionId { get; }

    RealtimeUser User { get; }

    /// <summary>Envía una trama de texto. Devuelve false si la conexión ya no está abierta.</summary>
    Task<bool> SendAsync(string json, CancellationToken cancellationToken);

    Task CloseAsync(int code, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// Conexiones abiertas en esta réplica (ADR-023: una réplica en el MVP). Un aviso solo se entrega a conexiones del
/// mismo tenant que el evento y, dentro del tenant, al usuario o al rol del destinatario.
/// </summary>
public sealed class ConnectionRegistry : IRealtimeConnections
{
    private readonly ConcurrentDictionary<Guid, IRealtimeClient> _clients = new();

    public int Count => _clients.Count;

    public void Add(IRealtimeClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _clients[client.ConnectionId] = client;
    }

    public void Remove(Guid connectionId) => _clients.TryRemove(connectionId, out _);

    public async Task<int> DeliverAsync(Delivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var json = JsonSerializer.Serialize(delivery.Notice, delivery.Notice.GetType(), RealtimeJson.Options);
        var sent = 0;
        foreach (var client in _clients.Values.Where(c => Matches(c.User, delivery)))
        {
            if (await client.SendAsync(json, cancellationToken).ConfigureAwait(false))
            {
                sent++;
            }
        }

        return sent;
    }

    /// <summary>Cierra todas las conexiones con 1001 cuando el servicio se detiene.</summary>
    public async Task CloseAllAsync(CancellationToken cancellationToken)
    {
        foreach (var client in _clients.Values)
        {
            await client.CloseAsync(CloseCodes.Restart, "reinicio", cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool Matches(RealtimeUser user, Delivery delivery) =>
        user.TenantId == delivery.TenantId && delivery.Recipient switch
        {
            UserRecipient u => u.UserId == user.UserId,
            RoleRecipient r => string.Equals(r.Role, user.Role, StringComparison.Ordinal),
            _ => false,
        };
}