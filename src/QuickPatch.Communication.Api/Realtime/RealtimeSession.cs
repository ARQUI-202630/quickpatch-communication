using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.Api.Realtime;

/// <summary>
/// Ciclo de una conexión de <c>/v1/realtime</c> según <c>asyncapi/realtime.v1.yaml</c>: espera la trama <c>auth</c>,
/// valida el token, registra la conexión y la cierra con el código del contrato cuando algo falla o el token vence.
/// El usuario, el tenant y el rol salen solo del token (RN-U3). El token no se registra en logs.
/// </summary>
public sealed partial class RealtimeSession(
    TokenValidator validator,
    ConnectionRegistry registry,
    IOptions<RealtimeOptions> options,
    TimeProvider time,
    ILogger<RealtimeSession> logger)
{
    private enum FrameKind
    {
        Text,
        Closed,
        TooBig,
        NotText,
    }

    public async Task RunAsync(WebSocket socket, CancellationToken aborted)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var settings = options.Value;

        // 1. Primera trama, con plazo.
        (FrameKind Kind, string? Text) first;
        using (var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(aborted))
        {
            authTimeout.CancelAfter(TimeSpan.FromSeconds(settings.AuthTimeoutSeconds));
            try
            {
                first = await ReadFrameAsync(socket, settings.MaxFrameBytes, authTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
            {
                await CloseQuietlyAsync(socket, CloseCodes.NoAuthentication, "sin-autenticacion", aborted).ConfigureAwait(false);
                LogClosed(logger, CloseCodes.NoAuthentication);
                return;
            }
        }

        if (first.Kind == FrameKind.Closed)
        {
            return;
        }

        if (first.Kind == FrameKind.TooBig)
        {
            await CloseQuietlyAsync(socket, CloseCodes.FrameTooBig, "trama-muy-grande", aborted).ConfigureAwait(false);
            LogClosed(logger, CloseCodes.FrameTooBig);
            return;
        }

        var token = first.Kind == FrameKind.Text ? ReadAuthToken(first.Text!) : null;
        if (token is null)
        {
            await CloseQuietlyAsync(socket, CloseCodes.InvalidFrame, "trama-invalida", aborted).ConfigureAwait(false);
            LogClosed(logger, CloseCodes.InvalidFrame);
            return;
        }

        // 2. Token.
        var user = await validator.ValidateAsync(token).ConfigureAwait(false);
        if (user is null)
        {
            await CloseQuietlyAsync(socket, CloseCodes.InvalidToken, "token-invalido", aborted).ConfigureAwait(false);
            LogClosed(logger, CloseCodes.InvalidToken);
            return;
        }

        if (!Roles.UsesRealtimeChannel(user.Role))
        {
            await CloseQuietlyAsync(socket, CloseCodes.RoleNotAllowed, "rol-no-admitido", aborted).ConfigureAwait(false);
            LogClosed(logger, CloseCodes.RoleNotAllowed);
            return;
        }

        // 3. Conexión registrada hasta que la app cierre, envíe otra trama o venza el token.
        using var client = new WebSocketClient(socket, user);
        var accepted = JsonSerializer.Serialize(new { type = "auth.accepted", expiresAt = user.ExpiresAt }, RealtimeJson.Options);
        if (!await client.SendAsync(accepted, aborted).ConfigureAwait(false))
        {
            return;
        }

        registry.Add(client);
        LogOpened(logger, user.UserId, user.TenantId, user.Role);
        try
        {
            using var untilExpiry = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            var remaining = user.ExpiresAt - time.GetUtcNow();
            untilExpiry.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            try
            {
                var next = await ReadFrameAsync(socket, settings.MaxFrameBytes, untilExpiry.Token).ConfigureAwait(false);
                if (next.Kind != FrameKind.Closed)
                {
                    // El contrato solo admite la trama auth: cualquier otra trama de la app es un error.
                    await client.CloseAsync(CloseCodes.InvalidFrame, "trama-invalida", aborted).ConfigureAwait(false);
                    LogClosed(logger, CloseCodes.InvalidFrame);
                }
            }
            catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
            {
                await client.CloseAsync(CloseCodes.TokenExpired, "token-vencido", aborted).ConfigureAwait(false);
                LogClosed(logger, CloseCodes.TokenExpired);
            }
        }
        catch (WebSocketException)
        {
            // La app se desconectó sin cerrar; se libera la conexión.
        }
        finally
        {
            registry.Remove(client.ConnectionId);
        }
    }

    internal static string? ReadAuthToken(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "auth"
                || !root.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = token.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static async Task CloseQuietlyAsync(WebSocket socket, int code, string reason, CancellationToken cancellationToken)
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

    private static async Task<(FrameKind Kind, string? Text)> ReadFrameAsync(WebSocket socket, int maxBytes, CancellationToken cancellationToken)
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
                    return (FrameKind.Closed, null);
                }

                total += result.Count;
                if (total > maxBytes)
                {
                    return (FrameKind.TooBig, null);
                }

                if (result.EndOfMessage)
                {
                    return result.MessageType == WebSocketMessageType.Text
                        ? (FrameKind.Text, Encoding.UTF8.GetString(buffer, 0, total))
                        : (FrameKind.NotText, null);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Canal en tiempo real abierto para el usuario {UserId} del tenant {TenantId} ({Role}).")]
    private static partial void LogOpened(ILogger logger, Guid userId, Guid tenantId, string role);

    [LoggerMessage(Level = LogLevel.Information, Message = "Canal en tiempo real cerrado con {Code}.")]
    private static partial void LogClosed(ILogger logger, int code);
}