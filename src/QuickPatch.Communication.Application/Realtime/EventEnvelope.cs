using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickPatch.Communication.Application.Realtime;

/// <summary>Sobre común de los eventos de Kafka (DD 8.4.1). <c>Data</c> se lee según <c>EventType</c>.</summary>
public sealed record EventEnvelope(
    Guid EventId,
    string EventType,
    int EventVersion,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    Guid TenantId,
    string Producer,
    JsonElement Data);

/// <summary>
/// Eventos de <c>quickpatch-kafka</c> que Communication convierte en avisos (DD 8.5). El estado de la solicitud sale de su
/// dueño, ServiceRequest (<c>service-request.created</c> y <c>service-request.status-changed</c>), y no de los eventos de
/// Matching, que ServiceRequest puede descartar por viejos (SCRUM-466).
/// </summary>
public static class EventTypes
{
    public const string ServiceRequestCreated = "service-request.created";
    public const string ServiceRequestStatusChanged = "service-request.status-changed";
    public const string OfferCreated = "matching.offer-created";

    public static IReadOnlyList<string> Consumed { get; } =
        [ServiceRequestCreated, ServiceRequestStatusChanged, OfferCreated];
}

public static class RealtimeJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}