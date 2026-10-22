using QuickPatch.Communication.Domain.Inbox;

namespace QuickPatch.Communication.UnitTests.Inbox;

public class NotificationTests
{
    private static Notification Nueva(DateTimeOffset createdAt, DateTimeOffset? readAt = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NotificationTypes.SolicitudCreada, "Título", "Mensaje", null, Guid.NewGuid(), createdAt, readAt);

    [Fact]
    public void MarkRead_GuardaMicrosegundosComoPostgreSql()
    {
        // 7 decimales, como entrega TimeProvider.GetUtcNow() en .NET.
        var now = new DateTimeOffset(2026, 10, 22, 15, 59, 35, TimeSpan.Zero).AddTicks(6_872_584);
        var n = Nueva(now);

        n.MarkRead(now);

        Assert.Equal(new DateTimeOffset(2026, 10, 22, 15, 59, 35, TimeSpan.Zero).AddTicks(6_872_580), n.ReadAt);
    }

    [Fact]
    public void MarkRead_EsIdempotenteYConservaElReadAtOriginal()
    {
        var n = Nueva(DateTimeOffset.UtcNow);
        var primera = new DateTimeOffset(2026, 10, 22, 16, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);

        n.MarkRead(primera);
        var guardado = n.ReadAt;
        n.MarkRead(primera.AddMinutes(5));

        Assert.Equal(guardado, n.ReadAt);
    }

    [Fact]
    public void Constructor_NormalizaCreatedAtYReadAtAMicrosegundos()
    {
        var baseTime = new DateTimeOffset(2026, 10, 22, 16, 0, 0, TimeSpan.Zero);

        var n = Nueva(baseTime.AddTicks(9_999_999), baseTime.AddTicks(5_000_009));

        Assert.Equal(baseTime.AddTicks(9_999_990), n.CreatedAt);
        Assert.Equal(baseTime.AddTicks(5_000_000), n.ReadAt);
    }
}
