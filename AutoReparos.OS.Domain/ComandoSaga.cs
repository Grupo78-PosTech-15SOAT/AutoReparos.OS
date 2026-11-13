using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AutoReparos.OS.Domain;

/// <summary>Comando persistido junto com a transição que o gerou; o despachador o publica depois.</summary>
public sealed class ComandoSaga
{
    private static readonly JsonSerializerOptions JsonNormalizacao = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [JsonInclude] public Guid EventId { get; private set; }
    [JsonInclude] public string Tipo { get; private set; } = string.Empty;
    [JsonInclude] public DestinoComando Destino { get; private set; }
    /// <summary>Número da etapa que este comando compensa; nulo para comandos que não são compensações.</summary>
    [JsonInclude] public int? EtapaCompensada { get; private set; }
    [JsonInclude] public string Corpo { get; private set; } = string.Empty;
    [JsonInclude] public DateTimeOffset CriadoEm { get; private set; }
    [JsonInclude] public int Tentativas { get; private set; }
    [JsonInclude] public DateTimeOffset? ProximaTentativaEm { get; private set; }
    [JsonInclude] public DateTimeOffset? EnviadoEm { get; private set; }
    [JsonInclude] public DateTimeOffset? EncaminhadoParaDlqEm { get; private set; }
    /// <summary>Comando que nunca chegou a ser enviado e foi abandonado (cancelamento antes da entrega).</summary>
    [JsonInclude] public DateTimeOffset? DescartadoEm { get; private set; }
    [JsonInclude] public string? UltimoErro { get; private set; }

    [JsonIgnore] public bool Pendente => EnviadoEm is null && EncaminhadoParaDlqEm is null && DescartadoEm is null;

    [JsonConstructor]
    private ComandoSaga() { }

    internal ComandoSaga(Guid eventId, string tipo, DestinoComando destino, int? etapaCompensada, string corpo, DateTimeOffset agora)
    {
        EventId = eventId;
        Tipo = tipo;
        Destino = destino;
        EtapaCompensada = etapaCompensada;
        Corpo = corpo;
        CriadoEm = agora;
    }

    /// <summary>Cópia para reenvio: mesmo conteúdo, novo <c>eventId</c> (também no corpo) e contadores zerados.</summary>
    internal ComandoSaga Reenvio(Guid novoEventId, DateTimeOffset agora)
    {
        var corpo = Corpo;
        if (JsonNode.Parse(Corpo) is JsonObject objeto)
        {
            objeto["eventId"] = novoEventId.ToString();
            corpo = objeto.ToJsonString(JsonNormalizacao);
        }
        return new ComandoSaga(novoEventId, Tipo, Destino, EtapaCompensada, corpo, agora);
    }

    internal void MarcarEnviado(DateTimeOffset agora)
    {
        EnviadoEm = agora;
        ProximaTentativaEm = null;
    }

    internal void RegistrarFalha(DateTimeOffset proximaTentativaEm, string erro)
    {
        Tentativas++;
        ProximaTentativaEm = proximaTentativaEm;
        UltimoErro = erro;
    }

    internal void RegistrarFalhaDlq(DateTimeOffset proximaTentativaEm, string erro)
    {
        Tentativas++;
        ProximaTentativaEm = proximaTentativaEm;
        UltimoErro = erro;
    }

    internal void MarcarDescartado(DateTimeOffset agora)
    {
        DescartadoEm = agora;
        ProximaTentativaEm = null;
    }

    internal void MarcarEncaminhadoParaDlq(DateTimeOffset agora, string erro)
    {
        EncaminhadoParaDlqEm = agora;
        ProximaTentativaEm = null;
        UltimoErro = erro;
    }

    internal void NormalizarTipo()
    {
        var novoTipo = TiposMensagem.NormalizarComando(Tipo);
        Tipo = novoTipo;
        if (JsonNode.Parse(Corpo) is JsonObject objeto && objeto.TryGetPropertyValue("tipo", out var noTipo) && noTipo?.GetValue<string>() is { } atual)
        {
            var normalizado = TiposMensagem.NormalizarComando(atual);
            if (normalizado != atual)
            {
                objeto["tipo"] = normalizado;
                Corpo = objeto.ToJsonString(JsonNormalizacao);
            }
        }
    }
}

public sealed record RegistroSaga(DateTimeOffset Em, string Evento, Guid? EventId, string Descricao);

/// <summary>Resultado de aplicar uma mensagem à Saga. Rejeições são registradas na própria Saga (idempotente).</summary>
/// <remarks><paramref name="Reentregavel"/>: a rejeição é só um adiamento (ex.: cancelamento durante a reserva); o <c>eventId</c> não é consumido e a mesma mensagem pode ser aplicada depois.</remarks>
public sealed record Transicao(bool Aceita, string? Motivo = null, string? CodigoAlerta = null, bool Reentregavel = false)
{
    public static readonly Transicao Ok = new(true);
}
