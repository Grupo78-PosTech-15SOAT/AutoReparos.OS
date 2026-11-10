using System.Globalization;
using System.Text.Json;
using AutoReparos.OS.Domain;

namespace AutoReparos.OS.Application.Saga;

/// <summary>Mensagem de entrada da Saga já validada (envelope e campos exigidos pelo <c>tipo</c>).</summary>
public sealed record MensagemSaga(
    Guid EventId,
    string Tipo,
    Guid OrdemServicoId,
    Guid? CorrelacaoId,
    DateTimeOffset? OcorreuEm,
    Guid? OrcamentoId,
    Guid? ClienteId,
    Guid? PagamentoId,
    StatusPagamento? ResultadoPagamento,
    string? Referencia,
    string? Motivo,
    Guid? ExecucaoId = null)
{
    /// <summary>Motivo válido: não vazio, até <see cref="SagaOS.MotivoMaximo"/> caracteres e sem o caractere NUL.</summary>
    public static bool MotivoValido(string? motivo) =>
        !string.IsNullOrWhiteSpace(motivo) && motivo.Length <= SagaOS.MotivoMaximo && !motivo.Contains((char)0);

    /// <summary>Id determinístico do cancelamento de uma OS: repetir o pedido de cancelamento reaproveita o mesmo <c>eventId</c> (idempotente).</summary>
    public static Guid EventIdCancelamento(Guid ordemServicoId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{TiposMensagem.OrdemServicoCancelar}:{ordemServicoId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    public static bool TentarLer(string? json, out MensagemSaga? mensagem, out string erro)
    {
        mensagem = null;
        if (string.IsNullOrWhiteSpace(json)) { erro = "Mensagem vazia."; return false; }
        try
        {
            using var documento = JsonDocument.Parse(json);
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object) { erro = "A mensagem deve ser um objeto JSON."; return false; }

            if (!Guid(raiz, "eventId", out var eventId) || eventId == System.Guid.Empty) { erro = "eventId ausente ou inválido."; return false; }
            var tipo = Texto(raiz, "tipo");
            if (tipo is null) { erro = "tipo ausente ou inválido."; return false; }
            if (!TiposMensagem.Entrada.Contains(tipo)) { erro = $"tipo desconhecido: {tipo}."; return false; }
            if (!Guid(raiz, "ordemServicoId", out var ordemId) || ordemId == System.Guid.Empty) { erro = "ordemServicoId ausente ou inválido."; return false; }

            Guid? correlacao = null;
            if (Existe(raiz, "correlacaoId"))
            {
                if (!Guid(raiz, "correlacaoId", out var c)) { erro = "correlacaoId inválido."; return false; }
                correlacao = c;
            }

            DateTimeOffset? ocorreuEm = null;
            if (Existe(raiz, "ocorreuEm"))
            {
                var texto = Texto(raiz, "ocorreuEm");
                if (texto is null || !DateTimeOffset.TryParse(texto, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var data)) { erro = "ocorreuEm inválido."; return false; }
                ocorreuEm = data.ToUniversalTime();
            }

            Guid? orcamentoId = null, clienteId = null, pagamentoId = null, execucaoId = null;
            StatusPagamento? status = null;
            var motivo = Texto(raiz, "motivo");
            if (tipo == TiposMensagem.OrdemServicoCancelar)
            {
                if (!MotivoValido(motivo)) { erro = $"motivo ausente ou inválido: informe de 1 a {SagaOS.MotivoMaximo} caracteres, sem o caractere NUL."; return false; }
            }
            else if (motivo is not null && (motivo.Length > SagaOS.MotivoMaximo || motivo.Contains((char)0)))
            {
                erro = $"motivo inválido: no máximo {SagaOS.MotivoMaximo} caracteres, sem o caractere NUL.";
                return false;
            }

            if (tipo is TiposMensagem.ExecucaoConcluida or TiposMensagem.ExecucaoFalhou or TiposMensagem.ExecucaoCancelada or TiposMensagem.ExecucaoCancelamentoRecusado && Existe(raiz, "execucaoId"))
            {
                if (!Guid(raiz, "execucaoId", out var e) || e == System.Guid.Empty) { erro = "execucaoId inválido."; return false; }
                execucaoId = e;
            }
            if (tipo is TiposMensagem.OrcamentoCancelado or TiposMensagem.OrcamentoCancelamentoRecusado && Existe(raiz, "orcamentoId"))
            {
                if (!Guid(raiz, "orcamentoId", out var o2) || o2 == System.Guid.Empty) { erro = "orcamentoId inválido."; return false; }
                orcamentoId = o2;
            }
            switch (tipo)
            {
                case TiposMensagem.OrcamentoAprovado:
                    if (!Guid(raiz, "orcamentoId", out var o) || o == System.Guid.Empty) { erro = "orcamentoId ausente ou inválido."; return false; }
                    if (!Guid(raiz, "clienteId", out var cl) || cl == System.Guid.Empty) { erro = "clienteId ausente ou inválido."; return false; }
                    orcamentoId = o;
                    clienteId = cl;
                    break;
                case TiposMensagem.PagamentoResultado:
                    if (!Guid(raiz, "pagamentoId", out var p) || p == System.Guid.Empty) { erro = "pagamentoId ausente ou inválido."; return false; }
                    var textoStatus = Texto(raiz, "status");
                    if (textoStatus is null || !char.IsLetter(textoStatus[0]) || !Enum.TryParse<StatusPagamento>(textoStatus, ignoreCase: false, out var s) || !Enum.IsDefined(s)) { erro = $"status de pagamento inválido: {textoStatus}."; return false; }
                    pagamentoId = p;
                    status = s;
                    break;
            }

            mensagem = new MensagemSaga(eventId, tipo, ordemId, correlacao, ocorreuEm, orcamentoId, clienteId, pagamentoId, status, Texto(raiz, "referencia"), motivo, execucaoId);
            erro = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            erro = $"JSON inválido: {ex.Message}";
            return false;
        }
    }

    private static bool Existe(JsonElement raiz, string nome) => Propriedade(raiz, nome, out var e) && e.ValueKind != JsonValueKind.Null;

    private static bool Guid(JsonElement raiz, string nome, out Guid valor)
    {
        valor = System.Guid.Empty;
        var texto = Texto(raiz, nome);
        return texto is not null && System.Guid.TryParse(texto, out valor);
    }

    // GetString lança InvalidOperationException para surrogate isolado; trata como ausente.
    private static string? Texto(JsonElement raiz, string nome)
    {
        if (!Propriedade(raiz, nome, out var elemento) || elemento.ValueKind != JsonValueKind.String) return null;
        try { return elemento.GetString(); }
        catch (InvalidOperationException) { return null; }
    }

    private static bool Propriedade(JsonElement raiz, string nome, out JsonElement elemento)
    {
        foreach (var propriedade in raiz.EnumerateObject())
        {
            if (!string.Equals(propriedade.Name, nome, StringComparison.OrdinalIgnoreCase)) continue;
            elemento = propriedade.Value;
            return true;
        }
        elemento = default;
        return false;
    }
}
