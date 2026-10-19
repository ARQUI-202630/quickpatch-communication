namespace QuickPatch.Communication.Infrastructure.Messaging;

/// <summary>Configuración de Kafka (sección <c>Kafka</c>). Sin <c>BootstrapServers</c> el servicio no consume (pruebas y local).</summary>
public sealed class KafkaOptions
{
    public const string Section = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;

    public string ConsumerGroupId { get; set; } = "communication-service";

    /// <summary>Grupo propio del buzón: lee desde el principio y confirma después de guardar (DD 5.22).</summary>
    public string InboxConsumerGroupId { get; set; } = "communication-inbox";

    public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);
}