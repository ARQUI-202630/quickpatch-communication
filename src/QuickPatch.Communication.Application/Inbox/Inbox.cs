using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Inbox;

namespace QuickPatch.Communication.Application.Inbox;

/// <summary>El buzón no tiene base configurada (desarrollo local sin PostgreSQL): la API responde 503.</summary>
public sealed class InboxUnavailableException() : Exception("El buzón no tiene base de datos configurada.");

/// <summary>Parámetros que violan el contrato; la API responde 400 con el detalle por campo.</summary>
public sealed class InboxValidationException(IReadOnlyDictionary<string, string[]> errors) : Exception("Parámetros inválidos.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

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

/// <summary>Consulta del buzón. <see cref="TenantId"/> y <see cref="UserId"/> vienen del JWT.</summary>
public sealed record ListNotificationsQuery(Guid TenantId, Guid UserId, bool UnreadOnly, int? Page, int? Size);

public sealed record NotificationList(IReadOnlyList<Notification> Items, int Page, int Size, int TotalItems, int UnreadCount);

/// <summary><c>GET /v1/notifications</c> (contrato 1.0.0): página desde 1 y hasta 100 por página (DD 8.1).</summary>
public sealed class ListNotificationsHandler(IInboxStore store)
{
    public const int DefaultSize = 20;
    public const int MaxSize = 100;

    public async Task<NotificationList> HandleAsync(ListNotificationsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var errors = new Dictionary<string, string[]>();
        var page = query.Page ?? 1;
        var size = query.Size ?? DefaultSize;
        if (page < 1)
        {
            errors["page"] = ["La página empieza en 1."];
        }

        if (size is < 1 or > MaxSize)
        {
            errors["size"] = [$"El tamaño de página va de 1 a {MaxSize}."];
        }

        if (errors.Count > 0)
        {
            throw new InboxValidationException(errors);
        }

        var result = await store.ListAsync(query.TenantId, query.UserId, query.UnreadOnly, (page - 1) * size, size, cancellationToken)
            .ConfigureAwait(false);
        return new NotificationList(result.Items, page, size, result.TotalItems, result.UnreadCount);
    }
}

/// <summary><c>POST /v1/notifications/{id}/read</c> (contrato 1.0.0): idempotente; de otro usuario o tenant, null (404).</summary>
public sealed class MarkNotificationReadHandler(IInboxStore store, TimeProvider clock)
{
    public Task<Notification?> HandleAsync(Guid tenantId, Guid userId, Guid id, CancellationToken cancellationToken) =>
        store.MarkReadAsync(tenantId, userId, id, clock.GetUtcNow(), cancellationToken);
}