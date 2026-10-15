using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QuickPatch.Communication.Infrastructure.Persistence;

/// <summary>
/// Fábrica para <c>dotnet ef</c> (generar migraciones). No se conecta a ninguna base:
/// la conexión real de las migraciones la da el paso de despliegue con el rol de migraciones.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CommunicationDbContext>
{
    public CommunicationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CommunicationDbContext>()
            .UseNpgsql("Host=localhost;Database=db_communication")
            .Options;
        return new CommunicationDbContext(options);
    }
}