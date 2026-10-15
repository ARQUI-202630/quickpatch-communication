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

/// <summary>Eventos de <c>quickpatch-kafka</c> que Communication convierte en avisos (DD 8.5) y en notificaciones del buzón (DD 8.2.6).</summary>
public static class EventTypes
{
    public const string ServiceRequestCreated = "service-request.created";
    public const string OfferCreated = "matching.offer-created";
    public const string TechnicianAssigned = "matching.technician-assigned";
    public const string NoTechnicianAvailable = "matching.no-technician-available";
    public const string SearchResumed = "matching.search-resumed";
    public const string TechnicianRegistered = "technician.registered";
    public const string VerificationChanged = "technician.verification-changed";

    /// <summary>Topics del canal en tiempo real.</summary>
    public static IReadOnlyList<string> Consumed { get; } =
        [ServiceRequestCreated, OfferCreated, TechnicianAssigned, NoTechnicianAvailable, SearchResumed];

    /// <summary>Topics del buzón (tabla de tipos de <c>communication.v1.yaml</c> 1.0.0).</summary>
    public static IReadOnlyList<string> Inbox { get; } =
        [.. Consumed, TechnicianRegistered, VerificationChanged];
}

/// <summary>Lectura de los campos de <c>data</c>; un campo que falta o no es válido hace inválido el evento.</summary>
public static class EventFields
{
    public static Guid Uuid(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty
            ? id
            : throw new InvalidEventException($"Falta o no es válido data.{name}.");

    public static DateTimeOffset Timestamp(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var at)
            ? at
            : throw new InvalidEventException($"Falta o no es válido data.{name}.");

    public static string Text(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidEventException($"Falta o no es válido data.{name}.");

    public static string? OptionalText(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Valida el sobre común (DD 8.4.1): versión 1, tenant y <c>data</c> como objeto.</summary>
    public static void EnsureValidEnvelope(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventVersion != 1 || envelope.TenantId == Guid.Empty || envelope.Data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidEventException($"Sobre inválido para {envelope.EventType}.");
        }
    }
}

public static class RealtimeJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}