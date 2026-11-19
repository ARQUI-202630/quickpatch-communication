using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Chat;
using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.Application.Chat;

/// <summary>Resultado de leer o escribir en el chat.</summary>
public abstract record ChatResult
{
    /// <summary>El usuario no participa, es de otro tenant o la solicitud aún no ha tenido técnico (404, RN-CH1).</summary>
    public sealed record NotFound : ChatResult;

    /// <summary>Historial con <c>writable</c>.</summary>
    public sealed record History(IReadOnlyList<ChatMessage> Items, int Page, int Size, int TotalItems, bool Writable) : ChatResult;

    public sealed record Sent(ChatMessage Message) : ChatResult;

    /// <summary>La solicitud terminó o se canceló: solo lectura (409 <c>chat-cerrado</c>).</summary>
    public sealed record Closed : ChatResult;

    /// <summary>La solicitud no tiene técnico en este momento (409 <c>chat-no-disponible</c>).</summary>
    public sealed record NoTechnician : ChatResult;
}