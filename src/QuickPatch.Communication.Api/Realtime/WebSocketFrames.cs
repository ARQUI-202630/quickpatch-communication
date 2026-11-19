using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace QuickPatch.Communication.Api.Realtime;

/// <summary>Tipo de la trama leída del WebSocket.</summary>
public enum WebSocketFrameKind
{
    Text,
    Closed,
    TooBig,
    NotText,
}

/// <summary>
/// Lectura y cierre de tramas del WebSocket, sin estado de la sesión: los usan la sesión y la conexión registrada
/// (SCRUM-656). Antes estaban en <see cref="RealtimeSession"/>, y <see cref="WebSocketClient"/> dependía de la sesión.
/// </summary>
public static class WebSocketFrames
{
    public static async Task CloseQuietlyAsync(WebSocket socket, int code, string reason, CancellationToken cancellationToken)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            await socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    public static async Task<(WebSocketFrameKind Kind, string? Text)> ReadAsync(WebSocket socket, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(maxBytes + 1);
        try
        {
            var total = 0;
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseQuietlyAsync(socket, (int)WebSocketCloseStatus.NormalClosure, string.Empty, cancellationToken).ConfigureAwait(false);
                    return (WebSocketFrameKind.Closed, null);
                }

                total += result.Count;
                if (total > maxBytes)
                {
                    return (WebSocketFrameKind.TooBig, null);
                }

                if (result.EndOfMessage)
                {
                    return result.MessageType == WebSocketMessageType.Text
                        ? (WebSocketFrameKind.Text, Encoding.UTF8.GetString(buffer, 0, total))
                        : (WebSocketFrameKind.NotText, null);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}