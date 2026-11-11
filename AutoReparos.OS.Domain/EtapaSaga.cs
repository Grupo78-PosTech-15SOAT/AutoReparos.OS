using System.Text.Json.Serialization;

namespace AutoReparos.OS.Domain;

public sealed class EtapaSaga
{
    [JsonInclude] public int Numero { get; private set; }
    [JsonInclude] public string Nome { get; private set; } = string.Empty;
    [JsonInclude] public SituacaoEtapa Situacao { get; private set; }
    /// <summary>Tipo do comando de compensação declarado para a etapa; nulo quando só há recuperação para frente.</summary>
    [JsonInclude] public string? Compensacao { get; private set; }
    [JsonInclude] public string? ResultadoCompensacao { get; private set; }
    [JsonInclude] public string? ErroCompensacao { get; private set; }
    [JsonInclude] public DateTimeOffset? AtualizadaEm { get; private set; }

    [JsonConstructor]
    private EtapaSaga() { }

    internal EtapaSaga(int numero, string nome, string? compensacao)
    {
        Numero = numero;
        Nome = nome;
        Compensacao = compensacao;
        Situacao = SituacaoEtapa.Pendente;
    }

    internal void Marcar(SituacaoEtapa situacao, DateTimeOffset agora)
    {
        Situacao = situacao;
        AtualizadaEm = agora;
    }

    internal string NormalizarCompensacao()
    {
        Compensacao = TiposMensagem.NormalizarComando(Compensacao!);
        return Compensacao;
    }

    internal void ConfirmarCompensacao(string resultado, DateTimeOffset agora)
    {
        Situacao = SituacaoEtapa.Compensada;
        ResultadoCompensacao = resultado;
        AtualizadaEm = agora;
    }

    internal void FalharCompensacao(string erro, DateTimeOffset agora)
    {
        Situacao = SituacaoEtapa.CompensacaoFalhou;
        ErroCompensacao = erro;
        AtualizadaEm = agora;
    }
}
