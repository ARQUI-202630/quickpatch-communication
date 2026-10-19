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
        EventFields.EnsureValidEnvelope(envelope);

        var tenant = envelope.TenantId;
        var data = envelope.Data;
        var at = envelope.OccurredAt;

        return envelope.EventType switch
        {
            EventTypes.ServiceRequestStatusChanged =>
                data.GetProperty("status").GetString() switch
                {
                    "asignado" => StatusChange(tenant, Uuid(data, "serviceRequestId"), "asignado", at, Uuid(data, "clientId"), Uuid(data, "technicianId")),
                    "en_espera" => StatusChange(tenant, Uuid(data, "serviceRequestId"), "en_espera", at, Uuid(data, "clientId")),
                    "buscando_tecnico" => StatusChange(tenant, Uuid(data, "serviceRequestId"), "buscando_tecnico", at, Uuid(data, "clientId")),
                    _ => [],
                },
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

    private static Guid Uuid(JsonElement data, string name) => EventFields.Uuid(data, name);

    private static DateTimeOffset Timestamp(JsonElement data, string name) => EventFields.Timestamp(data, name);
}