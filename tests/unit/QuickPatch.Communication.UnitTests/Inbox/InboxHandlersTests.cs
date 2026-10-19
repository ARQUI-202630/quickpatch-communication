using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Domain.Inbox;

namespace QuickPatch.Communication.UnitTests.Inbox;

public class InboxHandlersTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 16, 0, 0, TimeSpan.Zero);
    private readonly InMemoryInboxStore _store = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private Notification Add(int minutes, Guid? user = null, Guid? tenant = null, DateTimeOffset? readAt = null)
    {
        var n = new Notification(Guid.NewGuid(), tenant ?? _tenant, user ?? _user, NotificationTypes.SolicitudCreada, "t", "m", null,
            Guid.NewGuid(), T0.AddMinutes(minutes), readAt);
        _store.Rows.Add(n);
        return n;
    }

    [Fact]
    public async Task Listar_DeLaMasRecienteALaMasAntigua_ConElConteoDeNoLeidas()
    {
        var vieja = Add(1, readAt: T0);
        var nueva = Add(2);
        Add(3, user: Guid.NewGuid());
        Add(4, tenant: Guid.NewGuid(), user: _user);

        var result = await new ListNotificationsHandler(_store).HandleAsync(new(_tenant, _user, false, null, null), CancellationToken.None);

        Assert.Equal([nueva.Id, vieja.Id], result.Items.Select(n => n.Id));
        Assert.Equal((1, 20, 2, 1), (result.Page, result.Size, result.TotalItems, result.UnreadCount));
    }

    [Fact]
    public async Task ListarSoloNoLeidas_FiltraPeroElConteoEsDelBuzon()
    {
        Add(1, readAt: T0);
        var noLeida = Add(2);

        var result = await new ListNotificationsHandler(_store).HandleAsync(new(_tenant, _user, true, 1, 10), CancellationToken.None);

        Assert.Equal(noLeida.Id, Assert.Single(result.Items).Id);
        Assert.Equal(1, result.TotalItems);
        Assert.Equal(1, result.UnreadCount);
    }

    [Fact]
    public async Task ListarSegundaPagina_SaltaLasAnteriores()
    {
        for (var i = 0; i < 5; i++)
        {
            Add(i);
        }

        var result = await new ListNotificationsHandler(_store).HandleAsync(new(_tenant, _user, false, 2, 2), CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(T0.AddMinutes(2), result.Items[0].CreatedAt);
        Assert.Equal(5, result.TotalItems);
    }

    [Theory]
    [InlineData(0, 20, "page")]
    [InlineData(1, 0, "size")]
    [InlineData(1, 101, "size")]
    public async Task ListarConPaginaInvalida_Valida(int page, int size, string field)
    {
        var ex = await Assert.ThrowsAsync<InboxValidationException>(() =>
            new ListNotificationsHandler(_store).HandleAsync(new(_tenant, _user, false, page, size), CancellationToken.None));

        Assert.Equal([field], ex.Errors.Keys);
    }

    [Fact]
    public async Task MarcarLeida_EsIdempotenteYConservaLaPrimeraHora()
    {
        var n = Add(1);

        var first = await new MarkNotificationReadHandler(_store, new FixedClock(T0.AddHours(1))).HandleAsync(_tenant, _user, n.Id, CancellationToken.None);
        var second = await new MarkNotificationReadHandler(_store, new FixedClock(T0.AddHours(2))).HandleAsync(_tenant, _user, n.Id, CancellationToken.None);

        Assert.Equal(T0.AddHours(1), first!.ReadAt);
        Assert.Equal(T0.AddHours(1), second!.ReadAt);
    }

    [Fact]
    public async Task MarcarLeidaDeOtroUsuarioOTenant_NoLaEncuentra()
    {
        var n = Add(1);
        var handler = new MarkNotificationReadHandler(_store, new FixedClock(T0));

        Assert.Null(await handler.HandleAsync(_tenant, Guid.NewGuid(), n.Id, CancellationToken.None));
        Assert.Null(await handler.HandleAsync(Guid.NewGuid(), _user, n.Id, CancellationToken.None));
        Assert.Null(n.ReadAt);
    }
}