namespace AutoReparos.OS.Application.Saga;

public enum DesfechoMensagem
{
    Processada,
    EnviadaParaDlq
}

public sealed record ResultadoProcessamento(DesfechoMensagem Desfecho, ResultadoMensagem? Resultado, int Tentativas);

/// <summary>
/// Trata uma mensagem bruta da fila da Saga: valida, aplica no orquestrador e, em falha transitória, tenta até
/// <see cref="OpcoesSaga.MaxTentativas"/> vezes com backoff exponencial e jitter; depois envia à DLQ e alerta.
/// Mensagem malformada ou de tipo desconhecido vai direto à DLQ.
/// </summary>
public sealed class ProcessadorMensagemSaga(OrquestradorSaga orquestrador, IEnvioComandos envio, IAlertador alertador, OpcoesSaga opcoes)
{
    /// <summary>Se o envio à DLQ falhar, a exceção propaga: o chamador não deve apagar a mensagem da fila.</summary>
    public async Task<ResultadoProcessamento> ProcessarAsync(string? corpo, CancellationToken cancellationToken)
    {
        if (!MensagemSaga.TentarLer(corpo, out var mensagem, out var erro))
        {
            await envio.EnviarParaDlqAsync(corpo ?? string.Empty, $"Mensagem inválida: {erro}", cancellationToken);
            alertador.Alertar("saga.mensagem-invalida", $"Mensagem da Saga descartada para a DLQ: {erro}");
            return new ResultadoProcessamento(DesfechoMensagem.EnviadaParaDlq, null, 0);
        }

        for (var tentativa = 1; ; tentativa++)
        {
            try
            {
                var resultado = await orquestrador.ProcessarAsync(mensagem!, cancellationToken);
                return new ResultadoProcessamento(DesfechoMensagem.Processada, resultado, tentativa);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (tentativa >= opcoes.MaxTentativas)
                {
                    var motivo = $"Falha transitória esgotada após {tentativa} tentativas ao processar {mensagem!.Tipo}: {ex.Message}";
                    await envio.EnviarParaDlqAsync(corpo!, motivo, cancellationToken);
                    alertador.Alertar("saga.mensagem-esgotada", $"{motivo}. Mensagem enviada à DLQ; intervenção necessária.", mensagem.OrdemServicoId, mensagem.EventId);
                    return new ResultadoProcessamento(DesfechoMensagem.EnviadaParaDlq, null, tentativa);
                }
                await opcoes.Aguardar(opcoes.Atraso(tentativa), cancellationToken);
            }
        }
    }
}
