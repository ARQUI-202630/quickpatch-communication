namespace QuickPatch.Communication.Application.Realtime;

/// <summary>
/// Recuerda los últimos <c>eventId</c> procesados para no repetir un aviso cuando Kafka reentrega un evento (ADR-007).
/// Vive en memoria porque Communication no tiene base de datos todavía: tras un reinicio un evento puede repetirse,
/// y el aviso es idempotente (la app vuelve a pedir el mismo detalle). Con el buzón de ADR-024 pasa a
/// <c>processed_events</c>.
/// </summary>
public sealed class RecentEventIds(int capacity = 10_000)
{
    private readonly HashSet<Guid> _ids = [];
    private readonly Queue<Guid> _order = new();
    private readonly Lock _lock = new();

    /// <summary>Devuelve true si el evento es nuevo y lo registra; false si ya se procesó.</summary>
    public bool TryAdd(Guid eventId)
    {
        lock (_lock)
        {
            if (!_ids.Add(eventId))
            {
                return false;
            }

            _order.Enqueue(eventId);
            if (_order.Count > capacity)
            {
                _ids.Remove(_order.Dequeue());
            }

            return true;
        }
    }
}