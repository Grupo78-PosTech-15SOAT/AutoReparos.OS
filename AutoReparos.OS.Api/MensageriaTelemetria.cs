using System.Diagnostics;
using System.Text.Json;
using AutoReparos.OS.Domain;

namespace AutoReparos.OS.Api;

/// <summary>Spans de publicação e consumo das mensagens da Saga, ligados ao trace gravado no envelope (W3C).</summary>
public static class MensageriaTelemetria
{
    public const string NomeFonte = "AutoReparos.OS.Mensageria";

    public static readonly ActivitySource Fonte = new(NomeFonte);

    /// <summary>Span <c>Producer</c> "publicar {tipo}", filho do contexto do envelope quando ele existe.</summary>
    public static Activity? IniciarPublicacao(string corpo)
    {
        var envelope = Envelope.Ler(corpo);
        var atividade = envelope.Contexto is { } contexto
            ? Fonte.StartActivity($"publicar {envelope.Tipo}", ActivityKind.Producer, contexto)
            : Fonte.StartActivity($"publicar {envelope.Tipo}", ActivityKind.Producer);
        return Etiquetar(atividade, envelope);
    }

    /// <summary>
    /// Span <c>Consumer</c> "processar {tipo}" cujo pai é o contexto do envelope; sem contexto válido, é uma raiz nova (a
    /// <c>Activity</c> ambiente é descartada no fluxo assíncrono do chamador, que deve ser um método <c>async</c>).
    /// </summary>
    public static Activity? IniciarConsumo(string? corpo)
    {
        var envelope = Envelope.Ler(corpo);
        Activity? atividade;
        if (envelope.Contexto is { } contexto)
        {
            atividade = Fonte.StartActivity($"processar {envelope.Tipo}", ActivityKind.Consumer, contexto);
        }
        else
        {
            Activity.Current = null;
            atividade = Fonte.StartActivity($"processar {envelope.Tipo}", ActivityKind.Consumer, parentContext: default);
        }
        return Etiquetar(atividade, envelope);
    }

    private static Activity? Etiquetar(Activity? atividade, Envelope envelope)
    {
        if (atividade is null) return null;
        atividade.SetTag("messaging.system", "aws_sqs");
        if (envelope.EventId is not null) atividade.SetTag("messaging.message.id", envelope.EventId);
        if (envelope.OrdemServicoId is not null) atividade.SetTag("ordem_servico.id", envelope.OrdemServicoId);
        return atividade;
    }

    private sealed record Envelope(string Tipo, string? EventId, string? OrdemServicoId, ActivityContext? Contexto)
    {
        private static readonly Envelope Vazio = new("desconhecido", null, null, null);

        public static Envelope Ler(string? corpo)
        {
            if (string.IsNullOrWhiteSpace(corpo)) return Vazio;
            try
            {
                using var documento = JsonDocument.Parse(corpo);
                var raiz = documento.RootElement;
                if (raiz.ValueKind != JsonValueKind.Object) return Vazio;
                return new Envelope(Texto(raiz, "tipo") ?? Vazio.Tipo, Texto(raiz, "eventId"), Texto(raiz, "ordemServicoId"), ContextoTrace.Extrair(raiz));
            }
            catch (JsonException)
            {
                return Vazio;
            }
        }

        private static string? Texto(JsonElement raiz, string nome)
        {
            if (!raiz.TryGetProperty(nome, out var elemento) || elemento.ValueKind != JsonValueKind.String) return null;
            try { return elemento.GetString(); }
            catch (InvalidOperationException) { return null; }
        }
    }
}
