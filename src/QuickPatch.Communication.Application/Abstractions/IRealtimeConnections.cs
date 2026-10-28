using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.Application.Abstractions;

/// <summary>Entrega avisos a las conexiones abiertas. Lo implementa el registro de conexiones del API.</summary>
public interface IRealtimeConnections
{
    /// <summary>Envía el aviso a las conexiones del tenant y del destinatario; devuelve cuántas lo recibieron.</summary>
    Task<int> DeliverAsync(Delivery delivery, CancellationToken cancellationToken);
}