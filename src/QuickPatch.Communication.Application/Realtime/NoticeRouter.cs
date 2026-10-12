using System.Text.Json;

using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.Application.Realtime;

/// <summary>El evento no cumple su contrato: se registra y se descarta, sin reintentar.</summary>
public sealed class InvalidEventException(string message) : Exception(message);

/// <summary>
/// Decide a quién avisar por cada evento (DD 8.5, Cuadro de avisos). Solo usa el contenido del evento: los eventos
/// llevan <c>clientId</c> y <c>technicianId</c>, así que Communication no necesita tablas propias.
/// </summary>
public static class NoticeRouter
{
    public static IReadOnlyList<Delivery> Route(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventVersion != 1 || envelope.TenantId == Guid.Empty || envelope.Data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidEventException($"Sobre inválido para {envelope.EventType}.");
        }

        var tenant = envelope.TenantId;
        var data = envelope.Data;
        var at = envelope.OccurredAt;

        return envelope.EventType switch
        {
            EventTypes.ServiceRequestCreated => StatusChange(tenant, Uuid(data, "serviceRequestId"), "buscando_tecnico", at, Uuid(data, "clientId")),
            EventTypes.TechnicianAssigned => StatusChange(tenant, Uuid(data, "serviceRequestId"), "asignado", at, Uuid(data, "clientId"), Uuid(data, "technicianId")),
            EventTypes.NoTechnicianAvailable => StatusChange(tenant, Uuid(data, "serviceRequestId"), "en_espera", at, Uuid(data, "clientId")),
            EventTypes.SearchResumed => StatusChange(tenant, Uuid(data, "serviceRequestId"), "buscando_tecnico", at, Uuid(data, "clientId")),
            EventTypes.OfferCreated =>
            [
                new Delivery(
                    tenant,
                    new UserRecipient(Uuid(data, "technicianId")),
                    new OfferCreated(Uuid(data, "attemptId"), Uuid(data, "serviceRequestId"), Timestamp(data, "expiresAt"), at)),
            ],
            _ => [],
        };
    }

    private static List<Delivery> StatusChange(Guid tenant, Guid serviceRequestId, string status, DateTimeOffset at, params Guid[] users)
    {
        var notice = new ServiceRequestStatusChanged(serviceRequestId, status, at);
        var deliveries = users.Distinct().Select(u => new Delivery(tenant, new UserRecipient(u), notice)).ToList();
        deliveries.Add(new Delivery(tenant, new RoleRecipient(Roles.AdminTenant), new ServiceRequestCountsChanged(at)));
        return deliveries;
    }

    private static Guid Uuid(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty
            ? id
            : throw new InvalidEventException($"Falta o no es válido data.{name}.");

    private static DateTimeOffset Timestamp(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var at)
            ? at
            : throw new InvalidEventException($"Falta o no es válido data.{name}.");
}