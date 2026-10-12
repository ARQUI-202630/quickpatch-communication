using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.Application.Realtime;

/// <summary>Entrega avisos a las conexiones abiertas. Lo implementa el registro de conexiones del API.</summary>
public interface IRealtimeConnections
{
    /// <summary>Envía el aviso a las conexiones del tenant y del destinatario; devuelve cuántas lo recibieron.</summary>
    Task<int> DeliverAsync(Delivery delivery, CancellationToken cancellationToken);
}

public enum NotifyResult
{
    Delivered,
    Duplicate,
    Discarded,
}

/// <summary>Convierte un mensaje de Kafka en avisos: valida el sobre, descarta repetidos y entrega.</summary>
public sealed partial class RealtimeNotifier(IRealtimeConnections connections, RecentEventIds processed, ILogger<RealtimeNotifier> logger)
{
    public async Task<NotifyResult> HandleAsync(string message, CancellationToken cancellationToken)
    {
        EventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EventEnvelope>(message, RealtimeJson.Options);
        }
        catch (JsonException ex)
        {
            LogInvalid(logger, ex.Message);
            return NotifyResult.Discarded;
        }

        if (envelope is null || envelope.EventId == Guid.Empty || string.IsNullOrEmpty(envelope.EventType))
        {
            LogInvalid(logger, "sobre incompleto");
            return NotifyResult.Discarded;
        }

        IReadOnlyList<Delivery> deliveries;
        try
        {
            deliveries = NoticeRouter.Route(envelope);
        }
        catch (InvalidEventException ex)
        {
            LogInvalid(logger, ex.Message);
            return NotifyResult.Discarded;
        }

        if (!processed.TryAdd(envelope.EventId))
        {
            return NotifyResult.Duplicate;
        }

        var sent = 0;
        foreach (var delivery in deliveries)
        {
            sent += await connections.DeliverAsync(delivery, cancellationToken).ConfigureAwait(false);
        }

        LogDelivered(logger, envelope.EventType, envelope.EventId, envelope.TenantId, sent);
        return NotifyResult.Delivered;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Evento que no cumple el contrato; se descarta: {Reason}")]
    private static partial void LogInvalid(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EventType} {EventId} del tenant {TenantId}: {Sent} avisos enviados.")]
    private static partial void LogDelivered(ILogger logger, string eventType, Guid eventId, Guid tenantId, int sent);
}