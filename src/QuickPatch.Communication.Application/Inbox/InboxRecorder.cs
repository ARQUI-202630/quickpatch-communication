using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Inbox;

namespace QuickPatch.Communication.Application.Inbox;

public enum RecordResult
{
    Recorded,
    Ignored,
    Discarded,
}

/// <summary>
/// Convierte un mensaje de Kafka en notificaciones del buzón y las guarda en el tenant del evento. Un evento que no cumple
/// su contrato se registra y se descarta; si la base falla, la excepción sube para que el consumidor reintente el mensaje.
/// </summary>
public sealed partial class InboxRecorder(IInboxStore store, ILogger<InboxRecorder> logger)
{
    public async Task<RecordResult> HandleAsync(string message, CancellationToken cancellationToken)
    {
        EventEnvelope? envelope;
        IReadOnlyList<NewNotification> pending;
        try
        {
            envelope = JsonSerializer.Deserialize<EventEnvelope>(message, RealtimeJson.Options);
            if (envelope is null || envelope.EventId == Guid.Empty || string.IsNullOrEmpty(envelope.EventType))
            {
                throw new InvalidEventException("sobre incompleto");
            }

            pending = InboxRules.From(envelope);
        }
        catch (Exception ex) when (ex is JsonException or InvalidEventException)
        {
            LogInvalid(logger, ex.Message);
            return RecordResult.Discarded;
        }

        if (pending.Count == 0)
        {
            return RecordResult.Ignored;
        }

        var notifications = pending
            .DistinctBy(n => n.UserId)
            .Select(n => new Notification(
                Guid.CreateVersion7(), envelope.TenantId, n.UserId, n.Type, n.Title, n.Message, n.ServiceRequestId,
                envelope.EventId, envelope.OccurredAt))
            .ToList();
        var created = await store.AddAsync(envelope.TenantId, notifications, cancellationToken).ConfigureAwait(false);
        LogRecorded(logger, envelope.EventType, envelope.EventId, envelope.TenantId, created);
        return RecordResult.Recorded;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Evento que no cumple el contrato; no crea notificaciones: {Reason}")]
    private static partial void LogInvalid(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EventType} {EventId} del tenant {TenantId}: {Created} notificaciones nuevas.")]
    private static partial void LogRecorded(ILogger logger, string eventType, Guid eventId, Guid tenantId, int created);
}