using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using QuickPatch.Communication.Application;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Realtime;
using QuickPatch.Communication.Infrastructure;
using QuickPatch.Communication.Infrastructure.Messaging;

namespace QuickPatch.Communication.UnitTests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddApplication_RegistraLaCapaDeAplicacion()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddApplication());
    }

    [Fact]
    public void AddInfrastructure_RegistraLaCapaDeInfraestructura()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        Assert.Same(services, services.AddInfrastructure(configuration));
    }

    [Fact]
    public void ConBaseYKafka_RegistraLosTresConsumidores()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Communication"] = "Host=sin-base-en-pruebas-unitarias",
            ["Kafka:BootstrapServers"] = "kafka-de-pruebas:9092",
        }).Build();

        services.AddLogging().AddApplication().AddInfrastructure(configuration);
        services.AddSingleton<IRealtimeConnections>(new SinConexiones());
        using var provider = services.BuildServiceProvider();

        // El canal, el buzón y la réplica del chat. Con AddHostedService, el segundo consumidor persistente se perdía.
        var hosted = provider.GetServices<IHostedService>().ToList();
        Assert.Single(hosted.OfType<RealtimeEventsConsumer>());
        Assert.Equal(2, hosted.OfType<PersistentEventsConsumer>().Count());
        foreach (var service in hosted.OfType<IDisposable>())
        {
            service.Dispose();
        }
    }

    [Fact]
    public void AddApplication_SinServicios_LanzaExcepcion()
    {
        Assert.Throws<ArgumentNullException>(() => QuickPatch.Communication.Application.DependencyInjection.AddApplication(null!));
    }

    [Fact]
    public void AddInfrastructure_SinConfiguracion_LanzaExcepcion()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddInfrastructure(null!));
    }

    /// <summary>El registro de conexiones lo pone la API; aquí basta uno vacío.</summary>
    private sealed class SinConexiones : IRealtimeConnections
    {
        public Task<int> DeliverAsync(Delivery delivery, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}