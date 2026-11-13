using System.Globalization;
using AutoReparos.OS.Domain;

namespace AutoReparos.OS.Application.Saga;

public enum ResultadoRetentativa { Aceita, NaoEncontrada, EmAndamento, Indisponivel }

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
                // OS cancelada pela rota antes de a Saga existir: não reativar; compensar o orçamento de imediato e alertar.
                var jaCancelada = await ordens.ObterStatusAsync(mensagem.OrdemServicoId, cancellationToken) == StatusOS.Cancelada;
                if (jaCancelada)
                {
                    saga = SagaOS.IniciarJaCancelada(mensagem.OrdemServicoId, mensagem.OrcamentoId!.Value, mensagem.CorrelacaoId, mensagem.EventId, agora, mensagem.Itens);
                    transicao = new Transicao(true,
                        $"orcamento.aprovado.v1 recebido para a OS {mensagem.OrdemServicoId} já Cancelada: o orçamento será compensado e a OS não foi reativada.",
                        "saga.orcamento-aprovado-apos-cancelamento");
                }
                else
                {
                    saga = SagaOS.Iniciar(mensagem.OrdemServicoId, mensagem.OrcamentoId!.Value, mensagem.CorrelacaoId, mensagem.EventId, agora, mensagem.Itens);
                    transicao = Transicao.Ok;
                }
            }
            else
            {
                transicao = Divergencia(saga, mensagem) is { } divergencia
                    ? saga.RejeitarDivergencia(mensagem.Tipo, mensagem.EventId, divergencia, agora)
                    : Aplicar(saga, mensagem, agora);
                if (transicao.Aceita && mensagem.Tipo is TiposMensagem.ExecucaoConcluida or TiposMensagem.ExecucaoFalhou) saga.DefinirExecucaoId(mensagem.ExecucaoId);
            }

            switch (await GravarAsync(saga, new EventoProcessado(mensagem.EventId, mensagem.Tipo), cancellationToken))
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

    /// <summary><c>correlacaoId</c>, <c>orcamentoId</c> e <c>execucaoId</c> informados devem coincidir com os da Saga.</summary>
    private static string? Divergencia(SagaOS saga, MensagemSaga m)
    {
        if (m.Tipo == TiposMensagem.OrcamentoAprovado) return null;
        if (m.CorrelacaoId is { } correlacao && correlacao != saga.CorrelacaoId)
            return $"correlacaoId {correlacao} diverge do da Saga ({saga.CorrelacaoId}).";
        if (m.OrcamentoId is { } orcamento && orcamento != saga.OrcamentoId)
            return $"orcamentoId {orcamento} diverge do da Saga ({saga.OrcamentoId}).";
        if (m.ExecucaoId is { } execucao && saga.ExecucaoId is { } conhecida && execucao != conhecida)
            return $"execucaoId {execucao} diverge do da Saga ({conhecida}).";
        return null;
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
        TiposMensagem.InsumosReservados => saga.AplicarInsumosReservados(m.EventId, agora),
        TiposMensagem.InsumosReservaFalhou => saga.AplicarReservaFalhou(m.EventId, m.Motivo, agora),
        TiposMensagem.InsumosReservaCancelada => saga.AplicarCompensacaoConfirmada(m.Tipo, 5, m.EventId, agora),
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
            if (saga.CompensacaoSemResposta(agora)) await ExpirarCompensacaoAsync(saga, agora, cancellationToken);
            else if (saga.PassoSemResposta(agora)) await ExpirarPassoAsync(saga, agora, cancellationToken);
            else if (saga.SincronizacaoDevida(agora)) await SincronizarAsync(saga, agora, cancellationToken);
            else if (saga.ProximoComandoDevido(agora) is { } comando) await DespacharAsync(saga, comando, agora, cancellationToken);
            else if (saga.AlertaInsumosDevido(agora)) await AlertarInsumosAsync(saga, agora, cancellationToken);
            else return;
        }
    }

    /// <summary>Retentar a reserva de uma Saga que aguarda insumos (D-20): novo comando, 409 <c>saga_em_andamento</c> nos estados pendentes.</summary>
    public async Task<ResultadoRetentativa> RetentarReservaAsync(Guid ordemServicoId, string autor, string motivo, CancellationToken cancellationToken)
    {
        for (var conflitos = 0; conflitos < opcoes.MaxConflitosVersao; conflitos++)
        {
            var saga = await sagas.ObterAsync(ordemServicoId, cancellationToken);
            if (saga is null) return ResultadoRetentativa.NaoEncontrada;
            if (saga.Estado is EstadoSaga.ReservandoInsumos or EstadoSaga.Compensando) return ResultadoRetentativa.EmAndamento;
            var agora = tempo.GetUtcNow();
            if (!saga.Retentar(Guid.NewGuid(), autor, motivo, agora).Aceita) return ResultadoRetentativa.Indisponivel;
            var gravacao = await GravarAsync(saga, null, cancellationToken);
            if (gravacao == ResultadoGravacao.ConflitoVersao) continue;
            try { await ProgredirAsync(ordemServicoId, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            return ResultadoRetentativa.Aceita;
        }
        throw new ConflitoSagaException(ordemServicoId);
    }

    /// <summary>Grava a Saga com os prazos recalculados (compensação e passo), para que o despachador a retome no momento certo.</summary>
    private Task<ResultadoGravacao> GravarAsync(SagaOS saga, EventoProcessado? evento, CancellationToken cancellationToken)
    {
        saga.ArmarPrazoCompensacao(opcoes.PrazoCompensacao);
        saga.ArmarPrazoPasso(opcoes.PrazoReserva, opcoes.PrazoLiberacao);
        return sagas.GravarAsync(saga, evento, cancellationToken);
    }

    /// <summary>Reserva ou liberação sem resposta no prazo: reenvia e, esgotados os reenvios, compensa (C5, C9).</summary>
    private async Task ExpirarPassoAsync(SagaOS saga, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var (comando, reenviado, prazo) = saga.ExpirarPasso(agora, opcoes.MaxReenvios);
        if (await GravarAsync(saga, null, cancellationToken) != ResultadoGravacao.Gravada) return;
        var limite = prazo.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        if (reenviado)
            alertador.Alertar("saga.passo-reenviado", $"Sem resposta de {comando} na Saga da OS {saga.OrdemServicoId} até {limite}; passo reenviado ({saga.ReenviosPasso} de {opcoes.MaxReenvios}).", saga.OrdemServicoId);
        else
            alertador.Alertar("saga.passo-sem-resposta", $"Sem resposta de {comando} na Saga da OS {saga.OrdemServicoId} até {limite} depois de {opcoes.MaxReenvios} reenvios; compensando ({SagaOS.MotivoSemResposta}).", saga.OrdemServicoId);
    }

    private async Task AlertarInsumosAsync(SagaOS saga, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        saga.RegistrarAlertaInsumos(agora);
        if (await GravarAsync(saga, null, cancellationToken) != ResultadoGravacao.Gravada) return;
        alertador.Alertar("saga.aguardando-insumos", $"Saga da OS {saga.OrdemServicoId} segue aguardando insumos; retentar ou cancelar.", saga.OrdemServicoId);
    }

    /// <summary>Compensação sem confirmação no prazo: a Saga vai a <c>CompensacaoFalhou</c> e o alerta pede intervenção.</summary>
    private async Task ExpirarCompensacaoAsync(SagaOS saga, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        if (saga.ReenviarCompensacao(agora, opcoes.MaxReenvios)) { await GravarAsync(saga, null, cancellationToken); return; }
        var (etapa, compensacao, prazo) = saga.ExpirarCompensacao(agora);
        if (await GravarAsync(saga, null, cancellationToken) != ResultadoGravacao.Gravada) return;
        alertador.Alertar("saga.compensacao-sem-resposta",
            $"Compensação da etapa {etapa} ({compensacao}) sem confirmação até {prazo.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}; " +
            $"Saga da OS {saga.OrdemServicoId} em CompensacaoFalhou, intervenção necessária.", saga.OrdemServicoId);
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
            if (await GravarAsync(saga, null, cancellationToken) == ResultadoGravacao.Gravada)
                alertador.Alertar("saga.sincronizacao-os-falhou",
                    $"Falha ao sincronizar a OS {saga.OrdemServicoId} para {status} (Saga em {saga.Estado}, tentativa {saga.TentativasSincronizacao}): {ex.Message}. " +
                    $"Recuperação para frente: nova tentativa em {atraso.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s; nenhum estorno ou desfazimento de pagamento será executado.",
                    saga.OrdemServicoId);
            return;
        }

        saga.ConcluirSincronizacao(agora);
        if (status == StatusOS.OrcamentoAprovado) saga.AvancarParaExecucaoLiberada(resultado.ClienteId, resultado.VeiculoId, agora);
        // Em conflito, o próximo passo recarrega e repete a sincronização (idempotente).
        await GravarAsync(saga, null, cancellationToken);
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
                await GravarAsync(saga, null, cancellationToken);
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
                await GravarAsync(saga, null, cancellationToken);
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
            if (await GravarAsync(saga, null, cancellationToken) == ResultadoGravacao.Gravada)
                alertador.Alertar("saga.dlq-indisponivel", $"{motivo}. Falha também ao enviar à DLQ: {ex.Message}. O comando permanece pendente; intervenção necessária.", saga.OrdemServicoId, comando.EventId);
            return;
        }

        var compensacaoFalhou = saga.RegistrarComandoNaDlq(comando.EventId, erro, agora);
        if (await GravarAsync(saga, null, cancellationToken) != ResultadoGravacao.Gravada) return;
        alertador.Alertar("saga.comando-esgotado", $"{motivo}. Encaminhado à DLQ; intervenção necessária.", saga.OrdemServicoId, comando.EventId);
        if (compensacaoFalhou)
            alertador.Alertar("saga.compensacao-falhou", $"Compensação não entregue para a OS {saga.OrdemServicoId} ({comando.Tipo}); Saga em CompensacaoFalhou, intervenção necessária.", saga.OrdemServicoId, comando.EventId);
    }
}
