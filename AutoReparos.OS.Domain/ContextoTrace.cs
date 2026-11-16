using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoReparos.OS.Domain;

/// <summary>Contexto de trace W3C no envelope das mensagens: campos opcionais <c>traceparent</c> e <c>tracestate</c> na raiz.</summary>
public static class ContextoTrace
{
    public const string CampoTraceparent = "traceparent";
    public const string CampoTracestate = "tracestate";

    /// <summary>Grava o contexto da <see cref="Activity.Current"/> (formato W3C) no envelope; sem <c>Activity</c> corrente, não acrescenta nada.</summary>
    public static void Injetar(JsonObject envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var atual = Activity.Current;
        if (atual is null || atual.IdFormat != ActivityIdFormat.W3C || atual.Id is null) return;
        envelope[CampoTraceparent] = atual.Id;
        if (!string.IsNullOrEmpty(atual.TraceStateString)) envelope[CampoTracestate] = atual.TraceStateString;
    }

    /// <summary>Contexto remoto do envelope; nulo quando o <c>traceparent</c> falta ou é inválido.</summary>
    public static ActivityContext? Extrair(JsonElement raiz)
    {
        if (raiz.ValueKind != JsonValueKind.Object) return null;
        var traceparent = Texto(raiz, CampoTraceparent);
        if (string.IsNullOrEmpty(traceparent)) return null;
        return ActivityContext.TryParse(traceparent, Texto(raiz, CampoTracestate), isRemote: true, out var contexto) ? contexto : null;
    }

    private static string? Texto(JsonElement raiz, string nome)
    {
        if (!raiz.TryGetProperty(nome, out var elemento) || elemento.ValueKind != JsonValueKind.String) return null;
        try { return elemento.GetString(); }
        catch (InvalidOperationException) { return null; }
    }
}
