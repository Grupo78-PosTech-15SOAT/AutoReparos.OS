using System.Globalization;
using AutoReparos.OS.Domain;

namespace AutoReparos.OS.Application.Saga;

public enum ResultadoMensagem
{
    Aplicada,
    /// <summary>Transição inválida: registrada na Saga, sem mudar de estado.</summary>
    Rejeitada,
    /// <summary>O <c>eventId</c> já tinha sido processado; nenhum efeito repetido.</summary>
    Duplicada
}

/// <summary>
/// Orquestrador da Saga: aplica mensagens (persistindo estado, comandos pendentes e <c>eventId</c> na mesma transação) e
/// progride o trabalho pendente (sincronização da OS e publicação de comandos), sempre depois de persistir.
/// </summary>
public sealed class OrquestradorSaga(
    ISagaRepositorio sagas,
    IOrdemServicoSincronizador ordens,
    IEnvioComandos envio,
    IAlertador alertador,
    TimeProvider tempo,
    OpcoesSaga opcoes)
{
    private const int MaxPassos = 50;

    public async Task<ResultadoMensagem> ProcessarAsync(MensagemSaga mensagem, CancellationToken cancellationToken)
    {
        var resultado = await AplicarAsync(mensagem, cancellationToken);
        if (resultado != ResultadoMensagem.Duplicada)
        {
            // O trabalho já está persistido; se falhar agora o despachador retoma (e alerta) sem perder nada.
            try { await ProgredirAsync(mensagem.OrdemServicoId, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return resultado;
    }

    private async Task<ResultadoMensagem> AplicarAsync(MensagemSaga mensagem, CancellationToken cancellationToken)
    {
        for (var conflitos = 0; conflitos < opcoes.MaxConflitosVersao; conflitos++)
        {
            if (await sagas.EventoProcessadoAsync(mensagem.EventId, cancellationToken)) return ResultadoMensagem.Duplicada;

            var saga = await sagas.ObterAsync(mensagem.OrdemServicoId, cancellationToken);
            var agora = tempo.GetUtcNow();
            Transicao transicao;
            if (saga is null)
            {
                if (mensagem.Tipo != TiposMensagem.OrcamentoAprovado) throw new SagaInexistenteException(mensagem.OrdemServicoId, mensagem.Tipo);
                saga = SagaOS.Iniciar(mensagem.OrdemServicoId, mensagem.OrcamentoId!.Value, mensagem.CorrelacaoId, mensagem.EventId, agora);
                transicao = Transicao.Ok;
            }
            else
            {
                transicao = Aplicar(saga, mensagem, agora);
            }

            switch (await sagas.GravarAsync(saga, new EventoProcessado(mensagem.EventId, mensagem.Tipo), cancellationToken))
            {
                case ResultadoGravacao.EventoDuplicado:
                    return ResultadoMensagem.Duplicada;
                case ResultadoGravacao.ConflitoVersao:
                    continue;
            }

            if (transicao.CodigoAlerta is { } codigo)
                alertador.Alertar(codigo, transicao.Motivo ?? DescreverAlerta(codigo, saga, mensagem), saga.OrdemServicoId, mensagem.EventId);
            return transicao.Aceita ? ResultadoMensagem.Aplicada : ResultadoMensagem.Rejeitada;
        }
        throw new ConflitoSagaException(mensagem.OrdemServicoId);
    }

    private static Transicao Aplicar(SagaOS saga, MensagemSaga m, DateTimeOffset agora) => m.Tipo switch
    {
        TiposMensagem.OrcamentoAprovado => saga.AplicarOrcamentoAprovadoRepetido(m.EventId, agora),
        TiposMensagem.ExecucaoConcluida => saga.AplicarExecucaoConcluida(m.EventId, agora),
        TiposMensagem.ExecucaoFalhou => saga.AplicarExecucaoFalhou(m.EventId, m.Motivo, agora),
        TiposMensagem.ExecucaoLiberacaoRecusada => saga.AplicarLiberacaoRecusada(m.EventId, m.Motivo, agora),
        TiposMensagem.PagamentoResultado => saga.AplicarPagamento(m.EventId, m.PagamentoId!.Value, m.ResultadoPagamento!.Value, m.Referencia, agora),
        TiposMensagem.OrdemServicoCancelar => saga.AplicarCancelamento(m.EventId, m.Motivo, agora),
        TiposMensagem.OrcamentoCancelado => saga.AplicarCompensacaoConfirmada(m.Tipo, 1, m.EventId, agora),
        TiposMensagem.ExecucaoCancelada => saga.AplicarCompensacaoConfirmada(m.Tipo, 2, m.EventId, agora),
        TiposMensagem.OrcamentoCancelamentoRecusado => saga.AplicarCompensacaoRecusada(m.Tipo, 1, m.EventId, m.Motivo, agora),
        TiposMensagem.ExecucaoCancelamentoRecusado => saga.AplicarCompensacaoRecusada(m.Tipo, 2, m.EventId, m.Motivo, agora),
        _ => throw new InvalidOperationException($"Tipo sem tratamento: {m.Tipo}.")
    };

    private static string DescreverAlerta(string codigo, SagaOS saga, MensagemSaga m) =>
        $"{codigo}: Saga da OS {saga.OrdemServicoId} em {saga.Estado} ao processar {m.Tipo}; intervenção necessária. Motivo informado: {m.Motivo ?? "n/d"}.";

    /// <summary>Executa o trabalho pendente devido da Saga (um passo por vez, sempre relendo o estado persistido).</summary>
    public async Task ProgredirAsync(Guid ordemServicoId, CancellationToken cancellationToken)
    {
        for (var passo = 0; passo < MaxPassos; passo++)
        {
            var saga = await sagas.ObterAsync(ordemServicoId, cancellationToken);
            if (saga is null || !saga.TemTrabalhoPendente) return;
            var agora = tempo.GetUtcNow();
            if (saga.SincronizacaoDevida(agora)) await SincronizarAsync(saga, agora, cancellationToken);
            else if (saga.ProximoComandoDevido(agora) is { } comando) await DespacharAsync(saga, comando, agora, cancellationToken);
            else return;
        }
    }

    private async Task SincronizarAsync(SagaOS saga, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var status = saga.SincronizacoesPendentes[0];
        ResultadoSincronizacao resultado;
        try
        {
            resultado = await ordens.SincronizarStatusAsync(saga.OrdemServicoId, status, agora, cancellationToken);
            if (!resultado.Encontrada) throw new InvalidOperationException($"OS {saga.OrdemServicoId} não encontrada no Postgres.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recuperação para frente: nunca estorna nem desfaz pagamento; retenta com backoff (teto fixo) e alerta a cada falha.
            var atraso = opcoes.Atraso(saga.TentativasSincronizacao + 1, opcoes.AtrasoSincronizacaoMaximo);
            saga.FalharSincronizacao(agora + atraso, ex.Message, agora);
            if (await sagas.GravarAsync(saga, null, cancellationToken) == ResultadoGravacao.Gravada)
                alertador.Alertar("saga.sincronizacao-os-falhou",
                    $"Falha ao sincronizar a OS {saga.OrdemServicoId} para {status} (Saga em {saga.Estado}, tentativa {saga.TentativasSincronizacao}): {ex.Message}. " +
                    $"Recuperação para frente: nova tentativa em {atraso.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s; nenhum estorno ou desfazimento de pagamento será executado.",
                    saga.OrdemServicoId);
            return;
        }

        saga.ConcluirSincronizacao(agora);
        if (status == StatusOS.OrcamentoAprovado) saga.AvancarParaExecucaoLiberada(resultado.ClienteId, resultado.VeiculoId, agora);
        // Em conflito, o próximo passo recarrega e repete a sincronização (idempotente).
        await sagas.GravarAsync(saga, null, cancellationToken);
    }

    private async Task DespacharAsync(SagaOS saga, ComandoSaga comando, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        string erro;
        if (comando.Tentativas < opcoes.MaxTentativas)
        {
            try
            {
                await envio.EnviarAsync(comando.Destino, comando.Corpo, cancellationToken);
                saga.RegistrarEnvio(comando.EventId, agora);
                await sagas.GravarAsync(saga, null, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                erro = ex.Message;
            }
            var tentativa = comando.Tentativas + 1;
            if (tentativa < opcoes.MaxTentativas)
            {
                saga.RegistrarFalhaEnvio(comando.EventId, agora + opcoes.Atraso(tentativa), erro, agora);
                await sagas.GravarAsync(saga, null, cancellationToken);
                return;
            }
            saga.RegistrarFalhaEnvio(comando.EventId, agora, erro, agora);
        }
        else
        {
            erro = comando.UltimoErro ?? "tentativas esgotadas";
        }

        // Tentativas esgotadas: DLQ e alerta acionável.
        var motivo = $"Comando {comando.Tipo} não publicado após {opcoes.MaxTentativas} tentativas: {erro}";
        try
        {
            await envio.EnviarParaDlqAsync(comando.Corpo, motivo, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            saga.RegistrarFalhaDlq(comando.EventId, agora + opcoes.Atraso(opcoes.MaxTentativas, opcoes.AtrasoSincronizacaoMaximo), ex.Message, agora);
            if (await sagas.GravarAsync(saga, null, cancellationToken) == ResultadoGravacao.Gravada)
                alertador.Alertar("saga.dlq-indisponivel", $"{motivo}. Falha também ao enviar à DLQ: {ex.Message}. O comando permanece pendente; intervenção necessária.", saga.OrdemServicoId, comando.EventId);
            return;
        }

        var compensacaoFalhou = saga.RegistrarComandoNaDlq(comando.EventId, erro, agora);
        if (await sagas.GravarAsync(saga, null, cancellationToken) != ResultadoGravacao.Gravada) return;
        alertador.Alertar("saga.comando-esgotado", $"{motivo}. Encaminhado à DLQ; intervenção necessária.", saga.OrdemServicoId, comando.EventId);
        if (compensacaoFalhou)
            alertador.Alertar("saga.compensacao-falhou", $"Compensação não entregue para a OS {saga.OrdemServicoId} ({comando.Tipo}); Saga em CompensacaoFalhou, intervenção necessária.", saga.OrdemServicoId, comando.EventId);
    }
}
