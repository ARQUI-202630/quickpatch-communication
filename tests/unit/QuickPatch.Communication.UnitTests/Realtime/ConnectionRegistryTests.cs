using System.Text.Json;

using QuickPatch.Communication.Api.Realtime;
using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.UnitTests.Realtime;

public class ConnectionRegistryTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2026, 10, 12, 16, 10, 0, TimeSpan.Zero);

    internal sealed class FakeClient(Guid userId, Guid tenantId, string role, bool open = true) : IRealtimeClient
    {
        public Guid ConnectionId { get; } = Guid.NewGuid();

        public RealtimeUser User { get; } = new(userId, tenantId, role, DateTimeOffset.MaxValue);

        public List<string> Sent { get; } = [];

        public int? ClosedWith { get; private set; }

        public Task<bool> SendAsync(string json, CancellationToken cancellationToken)
        {
            if (!open)
            {
                return Task.FromResult(false);
            }

            Sent.Add(json);
            return Task.FromResult(true);
        }

        public Task CloseAsync(int code, string reason, CancellationToken cancellationToken)
        {
            ClosedWith = code;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AvisoAUnUsuario_SoloLlegaASusConexionesDelMismoTenant()
    {
        var user = Guid.NewGuid();
        var registry = new ConnectionRegistry();
        var telefono = new FakeClient(user, TenantA, Roles.Cliente);
        var tablet = new FakeClient(user, TenantA, Roles.Cliente);
        var mismoIdOtroTenant = new FakeClient(user, TenantB, Roles.Cliente);
        var otroUsuario = new FakeClient(Guid.NewGuid(), TenantA, Roles.Cliente);
        foreach (var c in new[] { telefono, tablet, mismoIdOtroTenant, otroUsuario })
        {
            registry.Add(c);
        }

        var sent = await registry.DeliverAsync(
            new Delivery(TenantA, new UserRecipient(user), new ServiceRequestStatusChanged(Guid.NewGuid(), "asignado", At)),
            CancellationToken.None);

        Assert.Equal(2, sent);
        Assert.Single(telefono.Sent);
        Assert.Single(tablet.Sent);
        Assert.Empty(mismoIdOtroTenant.Sent);
        Assert.Empty(otroUsuario.Sent);
        using var json = JsonDocument.Parse(telefono.Sent[0]);
        Assert.Equal("service-request.status-changed", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("asignado", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AvisoAUnRol_SoloLlegaAEseRolEnElTenantDelEvento()
    {
        var registry = new ConnectionRegistry();
        var adminA = new FakeClient(Guid.NewGuid(), TenantA, Roles.AdminTenant);
        var adminB = new FakeClient(Guid.NewGuid(), TenantB, Roles.AdminTenant);
        var clienteA = new FakeClient(Guid.NewGuid(), TenantA, Roles.Cliente);
        registry.Add(adminA);
        registry.Add(adminB);
        registry.Add(clienteA);

        var sent = await registry.DeliverAsync(
            new Delivery(TenantA, new RoleRecipient(Roles.AdminTenant), new ServiceRequestCountsChanged(At)),
            CancellationToken.None);

        Assert.Equal(1, sent);
        Assert.Single(adminA.Sent);
        Assert.Empty(adminB.Sent);
        Assert.Empty(clienteA.Sent);
    }

    [Fact]
    public async Task ConexionCerrada_NoCuentaComoEntregada()
    {
        var user = Guid.NewGuid();
        var registry = new ConnectionRegistry();
        registry.Add(new FakeClient(user, TenantA, Roles.Tecnico, open: false));

        var sent = await registry.DeliverAsync(
            new Delivery(TenantA, new UserRecipient(user), new OfferCreated(Guid.NewGuid(), Guid.NewGuid(), At.AddMinutes(3), At)),
            CancellationToken.None);

        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task RemoveYCloseAll()
    {
        var registry = new ConnectionRegistry();
        var a = new FakeClient(Guid.NewGuid(), TenantA, Roles.Cliente);
        var b = new FakeClient(Guid.NewGuid(), TenantA, Roles.Tecnico);
        registry.Add(a);
        registry.Add(b);
        registry.Remove(a.ConnectionId);

        Assert.Equal(1, registry.Count);
        await registry.CloseAllAsync(CancellationToken.None);
        Assert.Null(a.ClosedWith);
        Assert.Equal(CloseCodes.Restart, b.ClosedWith);
    }

    [Fact]
    public void Matches_DestinatarioDesconocidoNoCoincide() =>
        Assert.False(ConnectionRegistry.Matches(
            new RealtimeUser(Guid.NewGuid(), TenantA, Roles.Cliente, At),
            new Delivery(TenantA, new OtroDestinatario(), new ServiceRequestCountsChanged(At))));

    private sealed record OtroDestinatario : Recipient;
}