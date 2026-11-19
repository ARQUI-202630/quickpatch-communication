using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Inbox;

namespace QuickPatch.Communication.Application.Inbox;

/// <summary>El buzón no tiene base configurada (desarrollo local sin PostgreSQL): la API responde 503.</summary>
public sealed class InboxUnavailableException() : Exception("El buzón no tiene base de datos configurada.");

/// <summary>Parámetros que violan el contrato; la API responde 400 con el detalle por campo.</summary>
public sealed class InboxValidationException(IReadOnlyDictionary<string, string[]> errors) : Exception("Parámetros inválidos.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}