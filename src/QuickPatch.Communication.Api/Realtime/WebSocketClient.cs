using System.Net.WebSockets;
using System.Text;

namespace QuickPatch.Communication.Api.Realtime;

/// <summary>Conexión WebSocket autenticada. Serializa los envíos: un WebSocket no admite dos envíos a la vez.</summary>
public sealed class WebSocketClient(WebSocket socket, RealtimeUser user) : IRealtimeClient, IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public Guid ConnectionId { get; } = Guid.NewGuid();

    public RealtimeUser User { get; } = user;

    public async Task<bool> SendAsync(string json, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return false;
            }

            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (WebSocketException)
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseAsync(int code, string reason, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RealtimeSession.CloseQuietlyAsync(socket, code, reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose() => _sendLock.Dispose();
}