using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Chat;
using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.Application.Chat;

/// <summary>El chat no tiene base configurada (desarrollo local sin PostgreSQL): la API responde 503.</summary>
public sealed class ChatUnavailableException() : Exception("El chat no tiene base de datos configurada.");

/// <summary>Parámetros o cuerpo que violan el contrato; la API responde 400 con el detalle por campo.</summary>
public sealed class ChatValidationException(IReadOnlyDictionary<string, string[]> errors) : Exception("Datos inválidos.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}