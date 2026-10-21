using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Chat;
using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.Application.Chat;

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

/// <summary>El chat no tiene base configurada (desarrollo local sin PostgreSQL): la API responde 503.</summary>
public sealed class ChatUnavailableException() : Exception("El chat no tiene base de datos configurada.");

/// <summary>Parámetros o cuerpo que violan el contrato; la API responde 400 con el detalle por campo.</summary>
public sealed class ChatValidationException(IReadOnlyDictionary<string, string[]> errors) : Exception("Datos inválidos.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>
/// Llena <c>chat_participants</c> con <c>service-request.status-changed</c> (ADR-026). Los demás eventos se ignoran. Un
/// evento que no cumple su contrato se registra y se descarta; si la base falla, la excepción sube para que el consumidor
/// reintente el mensaje.
/// </summary>
public sealed partial class ChatParticipantsRecorder(IChatStore store, ILogger<ChatParticipantsRecorder> logger)
{
    public async Task<RecordResult> HandleAsync(string message, CancellationToken cancellationToken)
    {
        ChatParticipants participants;
        try
        {
            var envelope = JsonSerializer.Deserialize<EventEnvelope>(message, RealtimeJson.Options);
            if (envelope is null || envelope.EventId == Guid.Empty || string.IsNullOrEmpty(envelope.EventType))
            {
                throw new InvalidEventException("sobre incompleto");
            }

            if (envelope.EventType != EventTypes.ServiceRequestStatusChanged)
            {
                return RecordResult.Ignored;
            }

            EventFields.EnsureValidEnvelope(envelope);
            var data = envelope.Data;
            participants = new ChatParticipants(
                EventFields.Uuid(data, "serviceRequestId"),
                envelope.TenantId,
                EventFields.Uuid(data, "clientId"),
                data.TryGetProperty("technicianId", out var technician) && technician.ValueKind == JsonValueKind.String
                    ? EventFields.Uuid(data, "technicianId")
                    : null,
                EventFields.Text(data, "status"),
                EventFields.Timestamp(data, "changedAt"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidEventException)
        {
            LogInvalid(logger, ex.Message);
            return RecordResult.Discarded;
        }

        var applied = await store.ApplyAsync(participants, cancellationToken).ConfigureAwait(false);
        return applied ? RecordResult.Recorded : RecordResult.Ignored;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Evento que no cumple el contrato; no cambia el chat: {Reason}")]
    private static partial void LogInvalid(ILogger logger, string reason);
}

/// <summary>Resultado de leer o escribir en el chat.</summary>
public abstract record ChatResult
{
    /// <summary>El usuario no participa, es de otro tenant o la solicitud aún no ha tenido técnico (404, RN-CH1).</summary>
    public sealed record NotFound : ChatResult;

    /// <summary>Historial con <c>writable</c>.</summary>
    public sealed record History(IReadOnlyList<ChatMessage> Items, int Page, int Size, int TotalItems, bool Writable) : ChatResult;

    public sealed record Sent(ChatMessage Message) : ChatResult;

    /// <summary>La solicitud terminó o se canceló: solo lectura (409 <c>chat-cerrado</c>).</summary>
    public sealed record Closed : ChatResult;

    /// <summary>La solicitud no tiene técnico en este momento (409 <c>chat-no-disponible</c>).</summary>
    public sealed record NoTechnician : ChatResult;
}

/// <summary><c>GET /v1/service-requests/{id}/messages</c> (contrato 1.3.0): página desde 1 y hasta 100 por página.</summary>
public sealed class ListChatMessagesHandler(IChatStore store)
{
    public const int DefaultSize = 30;
    public const int MaxSize = 100;

    public async Task<ChatResult> HandleAsync(
        Guid tenantId, Guid userId, Guid serviceRequestId, int? page, int? size, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var pageNumber = page ?? 1;
        var pageSize = size ?? DefaultSize;
        if (pageNumber < 1)
        {
            errors["page"] = ["La página empieza en 1."];
        }

        if (pageSize is < 1 or > MaxSize)
        {
            errors["size"] = [$"El tamaño de página va de 1 a {MaxSize}."];
        }

        if (errors.Count > 0)
        {
            throw new ChatValidationException(errors);
        }

        var participants = await store.FindAsync(tenantId, serviceRequestId, cancellationToken).ConfigureAwait(false);
        if (participants?.RoleOf(userId) is null)
        {
            return new ChatResult.NotFound();
        }

        var result = await store.ListAsync(tenantId, serviceRequestId, (pageNumber - 1) * pageSize, pageSize, cancellationToken)
            .ConfigureAwait(false);
        return new ChatResult.History(result.Items, pageNumber, pageSize, result.TotalItems, participants.IsWritable);
    }
}

/// <summary>
/// <c>POST /v1/service-requests/{id}/messages</c> (contrato 1.3.0, ADR-026): guarda el mensaje y lo entrega por el canal
/// (<c>chat.message</c>) al otro participante y a las demás conexiones del autor. El autor y su papel salen del JWT y de
/// la réplica, nunca del cuerpo.
/// </summary>
public sealed partial class SendChatMessageHandler(
    IChatStore store, IRealtimeConnections connections, TimeProvider clock, ILogger<SendChatMessageHandler> logger)
{
    public async Task<ChatResult> HandleAsync(
        Guid tenantId, Guid userId, Guid serviceRequestId, string? text, CancellationToken cancellationToken)
    {
        var body = ChatMessage.NormalizeText(text)
            ?? throw new ChatValidationException(new Dictionary<string, string[]>
            {
                ["text"] = [$"Escribe entre 1 y {ChatMessage.MaxLength} caracteres."],
            });

        var participants = await store.FindAsync(tenantId, serviceRequestId, cancellationToken).ConfigureAwait(false);
        var role = participants?.RoleOf(userId);
        if (participants is null || role is null)
        {
            return new ChatResult.NotFound();
        }

        if (!participants.IsWritable)
        {
            return participants.IsClosed ? new ChatResult.Closed() : new ChatResult.NoTechnician();
        }

        var message = new ChatMessage(
            Guid.CreateVersion7(clock.GetUtcNow()), tenantId, serviceRequestId, userId, role, body, clock.GetUtcNow());
        await store.AddAsync(message, cancellationToken).ConfigureAwait(false);

        // La entrega en tiempo real es lo mejor posible: el mensaje ya quedó guardado y el otro lo ve al abrir el chat.
        var notice = new ChatMessageNotice(
            serviceRequestId, new ChatMessageFrame(message.Id, message.AuthorId, message.AuthorRole, message.Body, message.SentAt));
        var sent = 0;
        foreach (var recipient in new[] { participants.ClientId, participants.TechnicianId!.Value })
        {
            try
            {
                sent += await connections.DeliverAsync(
                    new Delivery(tenantId, new UserRecipient(recipient), notice), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDeliveryFailed(logger, serviceRequestId, ex);
            }
        }

        LogSent(logger, serviceRequestId, tenantId, sent);
        return new ChatResult.Sent(message);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Mensaje del chat de {ServiceRequestId} (tenant {TenantId}) entregado a {Sent} conexiones.")]
    private static partial void LogSent(ILogger logger, Guid serviceRequestId, Guid tenantId, int sent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo entregar por el canal un mensaje del chat de {ServiceRequestId}.")]
    private static partial void LogDeliveryFailed(ILogger logger, Guid serviceRequestId, Exception exception);
}