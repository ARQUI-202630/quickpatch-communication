using System.Text.Json;

using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.Application.Realtime;

/// <summary>
/// Decide a quién avisar por cada evento (DD 8.5, Cuadro de avisos). Solo usa el contenido del evento: los eventos
/// llevan <c>clientId</c> y <c>technicianId</c>, así que Communication no necesita tablas propias. El aviso de estado
/// sale de ServiceRequest, que solo publica <c>service-request.status-changed</c> cuando aplicó el cambio (SCRUM-466).
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
            EventTypes.ServiceRequestCreated => StatusChange(
                tenant, EventFields.Uuid(data, "serviceRequestId"), "buscando_tecnico", at, EventFields.Uuid(data, "clientId")),
            EventTypes.ServiceRequestStatusChanged => StatusChange(
                tenant, EventFields.Uuid(data, "serviceRequestId"), EventFields.Text(data, "status"), at,
                [EventFields.Uuid(data, "clientId"), .. OptionalUuid(data, "technicianId")]),
            EventTypes.OfferCreated =>
            [
                new Delivery(
                    tenant,
                    new UserRecipient(EventFields.Uuid(data, "technicianId")),
                    new OfferCreated(
                        EventFields.Uuid(data, "attemptId"), EventFields.Uuid(data, "serviceRequestId"),
                        EventFields.Timestamp(data, "expiresAt"), at)),
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

    /// <summary>El técnico es opcional: ausente o nulo si la solicitud no tiene uno; si viene, debe ser válido.</summary>
    internal static Guid[] OptionalUuid(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? [EventFields.Uuid(data, name)] : [];
}