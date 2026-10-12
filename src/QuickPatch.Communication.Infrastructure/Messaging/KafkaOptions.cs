namespace QuickPatch.Communication.Infrastructure.Messaging;

/// <summary>Configuración de Kafka (sección <c>Kafka</c>). Sin <c>BootstrapServers</c> el servicio no consume (pruebas y local).</summary>
public sealed class KafkaOptions
{
    public const string Section = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;

    public string ConsumerGroupId { get; set; } = "communication-service";

    public bool Enabled => !string.IsNullOrWhiteSpace(BootstrapServers);
}