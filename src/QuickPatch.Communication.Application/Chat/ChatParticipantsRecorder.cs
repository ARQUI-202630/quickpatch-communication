using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Chat;
using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.Application.Chat;

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