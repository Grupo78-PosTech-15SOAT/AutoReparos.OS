using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;

/// <summary>C4 e C5: compensações, transições do contrato e recuperação para frente do pagamento capturado.</summary>
public sealed class SagaFlowTests(SagaAmbiente ambiente) : SagaTestBase(ambiente)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private Task<ResultadoProcessamento> PagamentoAsync(Guid os, string status, string referencia = "pix-1", Componentes? em = null) =>
        ReceberAsync(TiposMensagem.PagamentoResultado, os, em, null, ("pagamentoId", Guid.NewGuid()), ("status", status), ("referencia", referencia));

    // ---- Transições do contrato ----

    [Fact]
    public async Task SagaTransitions_ExecutionConcludedWaitsForPaymentAndSyncsTheOrder()
    {
        var s = await IniciarSagaAsync();

        var resultado = await ReceberAsync(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId);

        Assert.Equal(ResultadoMensagem.Aplicada, resultado.Resultado);
        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.ExecucaoConcluida, saga.Estado);
        Assert.Equal(SituacaoEtapa.Concluida, saga.Etapa(3).Situacao);
        Assert.Equal(SituacaoEtapa.Pendente, saga.Etapa(4).Situacao);
        await AssertOSAsync(s.OrdemServicoId, "Concluida", "Concluida", "EmExecucao", "OrcamentoAprovado", "Recebida");
        Assert.Single(Atual.Envio.Enviados);
    }

    [Fact]
    public async Task SagaTransitions_CapturedPaymentConcludesTheSagaAndMarksTheOrderPaid()
    {
        var s = await IniciarSagaAsync();
        await ReceberAsync(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId);

        var resultado = await PagamentoAsync(s.OrdemServicoId, "Capturado", "pix-123");

        Assert.Equal(ResultadoMensagem.Aplicada, resultado.Resultado);
        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.PagamentoCapturado, saga.Estado);
        Assert.True(saga.Terminal);
        Assert.Equal(SituacaoEtapa.Concluida, saga.Etapa(4).Situacao);
        Assert.Empty(saga.SincronizacoesPendentes);
        Assert.Contains(saga.Registros, r => r.Evento == TiposMensagem.PagamentoResultado && r.Descricao.EndsWith("Capturado (referência pix-123).", StringComparison.Ordinal));
        await AssertOSAsync(s.OrdemServicoId, "Paga", "Concluida", "EmExecucao", "OrcamentoAprovado", "Paga", "Recebida");
        Assert.Single(Atual.Envio.Enviados);
        Assert.Empty(Atual.Alertas.Alertas);
    }

    [Theory]
    [InlineData("SemTentativa")]
    [InlineData("Pendente")]
    [InlineData("Rejeitado")]
    [InlineData("Cancelado")]
    [InlineData("Expirado")]
    [InlineData("Reembolsado")]
    public async Task SagaTransitions_NonCapturedPaymentResultOnlyRecordsHistory(string status)
    {
        var s = await IniciarSagaAsync();
        await ReceberAsync(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId);

        var resultado = await PagamentoAsync(s.OrdemServicoId, status, "ref-9");

        Assert.Equal(ResultadoMensagem.Aplicada, resultado.Resultado);
        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.ExecucaoConcluida, saga.Estado);
        Assert.Equal(SituacaoEtapa.Pendente, saga.Etapa(4).Situacao);
        Assert.EndsWith($"{status} (referência ref-9).", saga.Registros[^1].Descricao, StringComparison.Ordinal);
        await AssertOSAsync(s.OrdemServicoId, "Concluida", "Concluida", "EmExecucao", "OrcamentoAprovado", "Recebida");
        Assert.Empty(Atual.Alertas.Alertas);
    }

    [Fact]
    public async Task SagaTransitions_CapturedPaymentBeforeExecutionConcludedIsRegisteredAndAlertedWithoutRefund()
    {
        var s = await IniciarSagaAsync();

        var resultado = await PagamentoAsync(s.OrdemServicoId, "Capturado");

        Assert.Equal(ResultadoMensagem.Rejeitada, resultado.Resultado);
        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, saga.Estado);
        var alerta = Assert.Single(Atual.Alertas.Alertas);
        Assert.Equal("saga.pagamento-capturado-fora-de-ordem", alerta.Codigo);
        Assert.Equal(s.OrdemServicoId, alerta.OrdemServicoId);
        Assert.Contains("nenhum estorno é executado", alerta.Mensagem);
        Assert.Single(Atual.Envio.Tentativas);
    }

    [Fact]
    public async Task SagaTransitions_ReExecutionAfterInterventionReachesTheConcludedState()
    {
        var s = await IniciarSagaAsync();
        var execucaoId = Guid.NewGuid(); // a reexecução é a mesma ordem de execução: o id é estável
        await ReceberAsync(TiposMensagem.ExecucaoFalhou, s.OrdemServicoId, null, null, ("execucaoId", execucaoId), ("motivo", "peça indisponível"));
        Assert.Equal(EstadoSaga.IntervencaoRequerida, (await SagaAsync(s.OrdemServicoId)).Estado);

        await ReceberAsync(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId, null, null, ("execucaoId", execucaoId));

        Assert.Equal(EstadoSaga.ExecucaoConcluida, (await SagaAsync(s.OrdemServicoId)).Estado);
        await AssertOSAsync(s.OrdemServicoId, "Concluida", "Concluida", "EmExecucao", "IntervencaoRequerida", "OrcamentoAprovado", "Recebida");
    }

    // ---- C4: compensações ----

    [Fact]
    public async Task CompensatesEachFailedStage_ExecutionFailureDoesNotCompensate()
    {
        var s = await IniciarSagaAsync();

        var resultado = await ReceberAsync(TiposMensagem.ExecucaoFalhou, s.OrdemServicoId, null, null, ("execucaoId", Guid.NewGuid()), ("motivo", "peça indisponível"));

        Assert.Equal(ResultadoMensagem.Aplicada, resultado.Resultado);
        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.IntervencaoRequerida, saga.Estado);
        Assert.Equal([SituacaoEtapa.Concluida, SituacaoEtapa.Concluida, SituacaoEtapa.Pendente, SituacaoEtapa.Pendente], saga.Etapas.Select(e => e.Situacao));
        Assert.Single(saga.Comandos);
        Assert.Equal(["ordem-servico.liberada.v1"], Atual.Envio.TiposEnviados);
        await AssertOSAsync(s.OrdemServicoId, "IntervencaoRequerida", "EmExecucao", "IntervencaoRequerida", "OrcamentoAprovado", "Recebida");
        Assert.Empty(Atual.Alertas.Alertas);
    }

    [Fact]
    public async Task CompensatesEachFailedStage_ReleaseRefusalCompensatesOnlyTheApprovedBudget()
    {
        var s = await IniciarSagaAsync();

        await ReceberAsync(TiposMensagem.ExecucaoLiberacaoRecusada, s.OrdemServicoId, null, null, ("motivo", "oficina lotada"));

        var emCompensacao = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.Compensando, emCompensacao.Estado);
        Assert.Equal(SituacaoEtapa.Falhou, emCompensacao.Etapa(2).Situacao);
        Assert.Equal(SituacaoEtapa.Compensando, emCompensacao.Etapa(1).Situacao);
        Assert.Equal(["ordem-servico.liberada.v1", "billing.orcamento.cancelar.v1"], Atual.Envio.TiposEnviados);
        var comando = Atual.Envio.Enviados[1];
        Assert.Equal(DestinoComando.Billing, comando.Destino);
        Assert.Equal(["correlacaoId", "eventId", "motivo", "ocorreuEm", "orcamentoId", "ordemServicoId", "tipo"], Json.Chaves(comando.Corpo));
        var corpo = Json.Ler(comando.Corpo);
        Assert.Equal(s.OrcamentoId, corpo.GetProperty("orcamentoId").GetGuid());
        Assert.Equal(s.OrdemServicoId, corpo.GetProperty("ordemServicoId").GetGuid());
        Assert.Equal(s.CorrelacaoId, corpo.GetProperty("correlacaoId").GetGuid());
        Assert.Equal("Liberação recusada pelo Execution: oficina lotada.", corpo.GetProperty("motivo").GetString());

        await ReceberAsync(TiposMensagem.OrcamentoCancelado, s.OrdemServicoId, null, null, ("orcamentoId", s.OrcamentoId));

        var final = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.Compensada, final.Estado);
        Assert.True(final.Terminal);
        Assert.Equal(SituacaoEtapa.Compensada, final.Etapa(1).Situacao);
        Assert.Equal("confirmada", final.Etapa(1).ResultadoCompensacao);
        Assert.Null(final.Etapa(1).ErroCompensacao);
        Assert.Equal(2, Atual.Envio.Enviados.Count);
        await AssertOSAsync(s.OrdemServicoId, "Cancelada", "Cancelada", "EmExecucao", "OrcamentoAprovado", "Recebida");
        Assert.Empty(Atual.Alertas.Alertas);
    }

    [Fact]
    public async Task CompensatesEachFailedStage_CancellationCompensatesStagesTwoAndOneInReverseOrder()
    {
        var s = await IniciarSagaAsync();

        await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "cliente desistiu"));

        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.Compensando, saga.Estado);
        Assert.Equal([SituacaoEtapa.Concluida, SituacaoEtapa.Compensando], saga.Etapas.Take(2).Select(e => e.Situacao));
        Assert.Equal(["ordem-servico.liberada.v1", "execucao.cancelar.v1"], Atual.Envio.TiposEnviados);
        Assert.Equal([DestinoComando.Execucao, DestinoComando.Execucao], Atual.Envio.Enviados.Select(e => e.Destino));
        var cancelarExecucao = Json.Ler(Atual.Envio.Enviados[1].Corpo);
        Assert.Equal(["correlacaoId", "eventId", "motivo", "ocorreuEm", "ordemServicoId", "tipo"], Json.Chaves(Atual.Envio.Enviados[1].Corpo));
        Assert.Equal("cliente desistiu", cancelarExecucao.GetProperty("motivo").GetString());
        Assert.Equal(s.CorrelacaoId, cancelarExecucao.GetProperty("correlacaoId").GetGuid());

        // Confirmação da etapa 1 antes da etapa 2 é fora de ordem: registrada e ignorada.
        var foraDeOrdem = await ReceberAsync(TiposMensagem.OrcamentoCancelado, s.OrdemServicoId);
        Assert.Equal(ResultadoMensagem.Rejeitada, foraDeOrdem.Resultado);
        Assert.Equal(2, Atual.Envio.Enviados.Count);

        await ReceberAsync(TiposMensagem.ExecucaoCancelada, s.OrdemServicoId, null, null, ("execucaoId", Guid.NewGuid()));

        var apos2 = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.Compensando, apos2.Estado);
        Assert.Equal([SituacaoEtapa.Compensando, SituacaoEtapa.Compensada], apos2.Etapas.Take(2).Select(e => e.Situacao));
        Assert.Equal("confirmada", apos2.Etapa(2).ResultadoCompensacao);
        Assert.Equal(["ordem-servico.liberada.v1", "execucao.cancelar.v1", "billing.orcamento.cancelar.v1"], Atual.Envio.TiposEnviados);
        Assert.Equal(DestinoComando.Billing, Atual.Envio.Enviados[2].Destino);
        var cancelarBilling = Json.Ler(Atual.Envio.Enviados[2].Corpo);
        Assert.Equal(s.OrcamentoId, cancelarBilling.GetProperty("orcamentoId").GetGuid());
        Assert.Equal("cliente desistiu", cancelarBilling.GetProperty("motivo").GetString());

        await ReceberAsync(TiposMensagem.OrcamentoCancelado, s.OrdemServicoId, null, null, ("orcamentoId", s.OrcamentoId));

        var final = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.Compensada, final.Estado);
        Assert.Equal([SituacaoEtapa.Compensada, SituacaoEtapa.Compensada, SituacaoEtapa.Pendente, SituacaoEtapa.Pendente], final.Etapas.Select(e => e.Situacao));
        Assert.Equal(["confirmada", "confirmada", null, null], final.Etapas.Select(e => e.ResultadoCompensacao));
        Assert.Equal(3, Atual.Envio.Enviados.Count);
        Assert.Empty(final.SincronizacoesPendentes);
        await AssertOSAsync(s.OrdemServicoId, "Cancelada", "Cancelada", "EmExecucao", "OrcamentoAprovado", "Recebida");
        Assert.Empty(Atual.Alertas.Alertas);

        // Cancelar de novo uma Saga compensada é ignorado, sem novos comandos.
        var repetido = await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "de novo"));
        Assert.Equal(ResultadoMensagem.Rejeitada, repetido.Resultado);
        Assert.Equal(3, Atual.Envio.Enviados.Count);
    }

    [Fact]
    public async Task CompensatesEachFailedStage_CancellationDuringInterventionAlsoCompensatesStagesTwoAndOne()
    {
        var s = await IniciarSagaAsync();
        await ReceberAsync(TiposMensagem.ExecucaoFalhou, s.OrdemServicoId, null, null, ("motivo", "sem peça"));

        await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "desistência"));

        Assert.Equal(["ordem-servico.liberada.v1", "execucao.cancelar.v1"], Atual.Envio.TiposEnviados);
        Assert.Equal(EstadoSaga.Compensando, (await SagaAsync(s.OrdemServicoId)).Estado);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompensatesEachFailedStage_RefusedCompensationBecomesCompensationFailedWithAlert(bool refusedByExecution)
    {
        var s = await IniciarSagaAsync();
        int etapa;
        string motivo;
        if (refusedByExecution)
        {
            await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "desistência"));
            motivo = "execução já iniciada";
            await ReceberAsync(TiposMensagem.ExecucaoCancelamentoRecusado, s.OrdemServicoId, null, null, ("execucaoId", Guid.NewGuid()), ("motivo", motivo));
            etapa = 2;
        }
        else
        {
            await ReceberAsync(TiposMensagem.ExecucaoLiberacaoRecusada, s.OrdemServicoId, null, null, ("motivo", "sem vaga"));
            motivo = "orçamento já possui pagamento";
            await ReceberAsync(TiposMensagem.OrcamentoCancelamentoRecusado, s.OrdemServicoId, null, null, ("orcamentoId", s.OrcamentoId), ("motivo", motivo));
            etapa = 1;
        }

        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.CompensacaoFalhou, saga.Estado);
        Assert.True(saga.Terminal);
        Assert.Equal(SituacaoEtapa.CompensacaoFalhou, saga.Etapa(etapa).Situacao);
        Assert.Equal(motivo, saga.Etapa(etapa).ErroCompensacao);
        Assert.Null(saga.Etapa(etapa).ResultadoCompensacao);
        var alerta = Assert.Single(Atual.Alertas.Alertas);
        Assert.Equal("saga.compensacao-falhou", alerta.Codigo);
        Assert.Equal(s.OrdemServicoId, alerta.OrdemServicoId);
        // Nenhuma compensação adicional e a OS não é cancelada.
        Assert.Equal(2, Atual.Envio.Enviados.Count);
        Assert.Equal("EmExecucao", (await OSAsync(s.OrdemServicoId)).Status);
    }

    [Fact]
    public async Task CompensatesEachFailedStage_ExhaustedCompensationCommandIsRecordedAsFailure()
    {
        Atual.Envio.Falha = (_, corpo) => Json.Tipo(corpo) == "execucao.cancelar.v1" ? new IOException("fila indisponível") : null;
        var s = await IniciarSagaAsync();
        await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "desistência"));

        foreach (var atraso in new[] { 1.25, 2.5, 5, 10 })
        {
            Relogio.Avancar(TimeSpan.FromSeconds(atraso));
            Assert.Equal(1, await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None));
        }

        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.CompensacaoFalhou, saga.Estado);
        Assert.Equal(SituacaoEtapa.CompensacaoFalhou, saga.Etapa(2).Situacao);
        Assert.StartsWith("Comando de compensação esgotado: fila indisponível", saga.Etapa(2).ErroCompensacao, StringComparison.Ordinal);
        Assert.Equal(SituacaoEtapa.Concluida, saga.Etapa(1).Situacao);
        Assert.Equal(5, Atual.Envio.Tentativas.Count(t => Json.Tipo(t.Corpo) == "execucao.cancelar.v1"));
        var dlq = Assert.Single(Atual.Envio.Dlq);
        Assert.Equal(saga.Comandos[1].Corpo, dlq.Corpo);
        Assert.Equal(["saga.comando-esgotado", "saga.compensacao-falhou"], Atual.Alertas.Codigos);
        Assert.DoesNotContain("billing.orcamento.cancelar.v1", Atual.Envio.TiposEnviados);
    }

    [Fact]
    public async Task CompensatesEachFailedStage_CancellationRefusedAfterExecutionConcludedAndAfterPayment()
    {
        var s = await IniciarSagaAsync();
        await ReceberAsync(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId);

        var aposExecucao = await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "tarde demais"));

        Assert.Equal(ResultadoMensagem.Rejeitada, aposExecucao.Resultado);
        Assert.Equal(EstadoSaga.ExecucaoConcluida, (await SagaAsync(s.OrdemServicoId)).Estado);
        var alerta = Assert.Single(Atual.Alertas.Alertas);
        Assert.Equal("saga.cancelamento-recusado", alerta.Codigo);
        Assert.Equal("Cancelamento recusado: execução já concluída (ExecucaoConcluida); nenhuma compensação nem estorno é executado.", alerta.Mensagem);

        await PagamentoAsync(s.OrdemServicoId, "Capturado");
        var aposPagamento = await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "tarde demais"));

        Assert.Equal(ResultadoMensagem.Rejeitada, aposPagamento.Resultado);
        Assert.Equal(EstadoSaga.PagamentoCapturado, (await SagaAsync(s.OrdemServicoId)).Estado);
        Assert.Equal(["saga.cancelamento-recusado", "saga.cancelamento-recusado"], Atual.Alertas.Codigos);
        Assert.Equal(["ordem-servico.liberada.v1"], Atual.Envio.TiposEnviados);
        Assert.Equal("Paga", (await OSAsync(s.OrdemServicoId)).Status);
    }

    [Fact]
    public async Task CompensatesEachFailedStage_CancellationBeforeReleaseCompensatesOnlyStageOneAndNeverReleases()
    {
        Controle.FalhasRestantes = 100;
        var s = await IniciarSagaAsync();
        var inicial = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.OrcamentoAprovado, inicial.Estado);
        Assert.Empty(Atual.Envio.Tentativas);

        await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "erro de cadastro"));
        Assert.Equal(["billing.orcamento.cancelar.v1"], Atual.Envio.TiposEnviados);
        await ReceberAsync(TiposMensagem.OrcamentoCancelado, s.OrdemServicoId);

        var compensada = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.Compensada, compensada.Estado);
        Assert.Equal(["OrcamentoAprovado", "Cancelada"], compensada.SincronizacoesPendentes);

        // A OS volta a sincronizar: aplica os dois status pendentes, em ordem, e não libera a execução.
        Controle.FalhasRestantes = 0;
        Relogio.Avancar(TimeSpan.FromHours(1));
        await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None);

        var final = await SagaAsync(s.OrdemServicoId);
        Assert.Empty(final.SincronizacoesPendentes);
        Assert.Equal(EstadoSaga.Compensada, final.Estado);
        Assert.Equal(["billing.orcamento.cancelar.v1"], Atual.Envio.TiposEnviados);
        await AssertOSAsync(s.OrdemServicoId, "Cancelada", "Cancelada", "OrcamentoAprovado", "Recebida");
    }

    // ---- Cancelamento: liberada pendente, ordem dos comandos, prazo e ids divergentes ----

    private static bool EhLiberada(string corpo) => Json.Tipo(corpo) == "ordem-servico.liberada.v1";

    [Fact]
    public async Task CompensatesEachFailedStage_CancellationDiscardsAPendingReleaseInsteadOfCompensatingIt()
    {
        Atual.Envio.Falha = (_, corpo) => EhLiberada(corpo) ? new IOException("SQS fora do ar") : null;
        Controle.AplicaA = status => status == "EmExecucao";
        Controle.FalhasRestantes = 100;
        var s = await IniciarSagaAsync();
        var antes = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, antes.Estado);
        Assert.True(Assert.Single(antes.Comandos).Pendente);
        Assert.Equal(["EmExecucao"], antes.SincronizacoesPendentes);

        await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "cliente desistiu"));

        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.Compensando, saga.Estado);
        Assert.Equal([SituacaoEtapa.Compensando, SituacaoEtapa.Pendente, SituacaoEtapa.Pendente, SituacaoEtapa.Pendente], saga.Etapas.Select(e => e.Situacao));
        var liberada = saga.Comandos[0];
        Assert.False(liberada.Pendente);
        Assert.Equal(T0, liberada.DescartadoEm);
        Assert.Null(liberada.EnviadoEm);
        Assert.Equal(1, liberada.Tentativas);
        Assert.Empty(saga.SincronizacoesPendentes);
        Assert.Equal(0, saga.TentativasSincronizacao);
        Assert.Single(saga.Registros, r => r.Evento == "comando.descartado" && r.EventId == liberada.EventId
            && r.Descricao == "Cancelamento antes da entrega: o comando ordem-servico.liberada.v1 pendente foi descartado; a etapa 2 não é compensada.");
        // A liberada nunca é reenviada; só a compensação da etapa 1 sai.
        Assert.Equal(["ordem-servico.liberada.v1", "billing.orcamento.cancelar.v1"], Atual.Envio.Tentativas.Select(t => Json.Tipo(t.Corpo)));
        Assert.Equal(["billing.orcamento.cancelar.v1"], Atual.Envio.TiposEnviados);
        Assert.DoesNotContain("execucao.cancelar.v1", Atual.Envio.TiposEnviados);

        await ReceberAsync(TiposMensagem.OrcamentoCancelado, s.OrdemServicoId, null, null, ("orcamentoId", s.OrcamentoId));

        var final = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.Compensada, final.Estado);
        Assert.Equal([SituacaoEtapa.Compensada, SituacaoEtapa.Pendente, SituacaoEtapa.Pendente, SituacaoEtapa.Pendente], final.Etapas.Select(e => e.Situacao));
        await AssertOSAsync(s.OrdemServicoId, "Cancelada", "Cancelada", "OrcamentoAprovado", "Recebida");
        Relogio.Avancar(TimeSpan.FromHours(1));
        Assert.Equal(0, await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None));
        Assert.Equal(2, Atual.Envio.Tentativas.Count);
    }

    [Fact]
    public async Task CompensatesEachFailedStage_CancellationCommandNeverOvertakesAPendingCommandInBackoff()
    {
        Atual.Envio.Falha = (_, corpo) => EhLiberada(corpo) ? new IOException("SQS fora do ar") : null;
        var s = await IniciarSagaAsync();
        // A execução recebeu a liberada (a confirmação do envio se perdeu) e falhou: a liberada segue pendente em backoff.
        await ReceberAsync(TiposMensagem.ExecucaoFalhou, s.OrdemServicoId, null, null, ("execucaoId", Guid.NewGuid()), ("motivo", "sem peça"));
        Assert.True(Assert.Single((await SagaAsync(s.OrdemServicoId)).Comandos).Pendente);

        await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "desistência"));

        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.Compensando, saga.Estado);
        Assert.Equal(["ordem-servico.liberada.v1", "execucao.cancelar.v1"], saga.Comandos.Select(c => c.Tipo));
        Assert.Empty(Atual.Envio.Enviados);
        Assert.Equal(["ordem-servico.liberada.v1"], Atual.Envio.Tentativas.Select(t => Json.Tipo(t.Corpo)));
        Assert.Equal(T0.AddSeconds(1.25), saga.Comandos[0].ProximaTentativaEm);
        Assert.Equal(0, await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None));

        Atual.Envio.Falha = (_, _) => null;
        Relogio.Avancar(TimeSpan.FromSeconds(1.25));
        Assert.Equal(1, await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None));

        Assert.Equal(["ordem-servico.liberada.v1", "execucao.cancelar.v1"], Atual.Envio.TiposEnviados);
        Assert.All((await SagaAsync(s.OrdemServicoId)).Comandos, c => Assert.False(c.Pendente));
    }

    [Fact]
    public async Task CompensatesEachFailedStage_CompensationWithoutConfirmationFailsAtTheDeadlineWithAnAlert()
    {
        var s = await IniciarSagaAsync();
        await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "cliente desistiu"));
        var emCompensacao = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.Compensando, emCompensacao.Estado);
        Assert.Equal(T0.AddMinutes(15), emCompensacao.PrazoCompensacaoEm);
        Assert.False(emCompensacao.Comandos[1].Pendente);
        var item = await ItemBrutoAsync(s.OrdemServicoId);
        Assert.Equal("S", item["trabalhoPendente"].S);
        Assert.Equal("2026-11-10T12:15:00.0000000Z", item["proximaTentativaEm"].S);

        Relogio.Avancar(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(59));
        Assert.Equal(0, await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None));
        Assert.Equal(EstadoSaga.Compensando, (await SagaAsync(s.OrdemServicoId)).Estado);
        Assert.Empty(Atual.Alertas.Alertas);

        Relogio.Avancar(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None));

        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.CompensacaoFalhou, saga.Estado);
        Assert.True(saga.Terminal);
        Assert.Null(saga.PrazoCompensacaoEm);
        Assert.Equal([SituacaoEtapa.Concluida, SituacaoEtapa.CompensacaoFalhou, SituacaoEtapa.Pendente, SituacaoEtapa.Pendente], saga.Etapas.Select(e => e.Situacao));
        Assert.Equal("Sem confirmação do participante até 2026-11-10T12:15:00Z (prazo vencido).", saga.Etapa(2).ErroCompensacao);
        var alerta = Assert.Single(Atual.Alertas.Alertas);
        Assert.Equal("saga.compensacao-sem-resposta", alerta.Codigo);
        Assert.Equal(s.OrdemServicoId, alerta.OrdemServicoId);
        Assert.Equal($"Compensação da etapa 2 (execucao.cancelar.v1) sem confirmação até 2026-11-10T12:15:00Z; Saga da OS {s.OrdemServicoId} em CompensacaoFalhou, intervenção necessária.", alerta.Mensagem);
        Assert.Equal(["ordem-servico.liberada.v1", "execucao.cancelar.v1"], Atual.Envio.TiposEnviados);
        Assert.Equal("EmExecucao", (await OSAsync(s.OrdemServicoId)).Status);

        // Estado terminal: nada mais a fazer, sem novo alerta; confirmação tardia é rejeitada.
        Assert.Equal(0, await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None));
        var tardia = await ReceberAsync(TiposMensagem.ExecucaoCancelada, s.OrdemServicoId);
        Assert.Equal(ResultadoMensagem.Rejeitada, tardia.Resultado);
        Assert.Equal(EstadoSaga.CompensacaoFalhou, (await SagaAsync(s.OrdemServicoId)).Estado);
        Assert.Single(Atual.Alertas.Alertas);
    }

    [Fact]
    public async Task CompensatesEachFailedStage_DeadlineIsConfigurableAndRestartsForEachCompensatedStage()
    {
        var curto = NovosComponentes(prazoCompensacao: TimeSpan.FromMinutes(2));
        var s = await IniciarSagaAsync(curto);
        await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, curto, null, ("motivo", "desistência"));
        Assert.Equal(T0.AddMinutes(2), (await SagaAsync(s.OrdemServicoId, curto)).PrazoCompensacaoEm);

        Relogio.Avancar(TimeSpan.FromSeconds(90));
        await ReceberAsync(TiposMensagem.ExecucaoCancelada, s.OrdemServicoId, curto, null, ("execucaoId", Guid.NewGuid()));
        var etapa1 = await SagaAsync(s.OrdemServicoId, curto);
        Assert.Equal(T0.AddSeconds(90).AddMinutes(2), etapa1.PrazoCompensacaoEm);
        Assert.Equal(["ordem-servico.liberada.v1", "execucao.cancelar.v1", "billing.orcamento.cancelar.v1"], curto.Envio.TiposEnviados);

        // O prazo da etapa 2 (T0 + 2 min) já passou, mas o da etapa 1 reiniciou na confirmação.
        Relogio.Avancar(TimeSpan.FromSeconds(90));
        Assert.Equal(0, await curto.Despachador.ExecutarCicloAsync(CancellationToken.None));
        Assert.Equal(EstadoSaga.Compensando, (await SagaAsync(s.OrdemServicoId, curto)).Estado);

        Relogio.Avancar(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await curto.Despachador.ExecutarCicloAsync(CancellationToken.None));

        var saga = await SagaAsync(s.OrdemServicoId, curto);
        Assert.Equal(EstadoSaga.CompensacaoFalhou, saga.Estado);
        Assert.Equal([SituacaoEtapa.CompensacaoFalhou, SituacaoEtapa.Compensada, SituacaoEtapa.Pendente, SituacaoEtapa.Pendente], saga.Etapas.Select(e => e.Situacao));
        Assert.Equal("Sem confirmação do participante até 2026-11-10T12:03:30Z (prazo vencido).", saga.Etapa(1).ErroCompensacao);
        var alerta = Assert.Single(curto.Alertas.Alertas);
        Assert.Equal("saga.compensacao-sem-resposta", alerta.Codigo);
        Assert.StartsWith("Compensação da etapa 1 (billing.orcamento.cancelar.v1) sem confirmação até 2026-11-10T12:03:30Z;", alerta.Mensagem);
    }

    [Fact]
    public async Task CompensatesEachFailedStage_ConfirmationWithTheSagaIdsIsAcceptedAndDivergentIdsAreRejectedWithAlert()
    {
        var s = await IniciarSagaAsync();
        var execucaoId = Guid.NewGuid();
        var outraCorrelacao = Guid.NewGuid();

        // correlacaoId divergente
        var (corpoCorrelacao, _) = Mensagem(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId, outraCorrelacao, null, ("execucaoId", execucaoId));
        var divergente = await Atual.Processador.ProcessarAsync(corpoCorrelacao, CancellationToken.None);
        Assert.Equal(ResultadoMensagem.Rejeitada, divergente.Resultado);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, (await SagaAsync(s.OrdemServicoId)).Estado);
        var alertaCorrelacao = Assert.Single(Atual.Alertas.Alertas);
        Assert.Equal("saga.mensagem-divergente", alertaCorrelacao.Codigo);
        Assert.Equal($"correlacaoId {outraCorrelacao} diverge do da Saga ({s.CorrelacaoId}).", alertaCorrelacao.Mensagem);
        var (corpoCerto, _) = Mensagem(TiposMensagem.ExecucaoFalhou, s.OrdemServicoId, s.CorrelacaoId, null, ("execucaoId", execucaoId), ("motivo", "sem peça"));
        Assert.Equal(ResultadoMensagem.Aplicada, (await Atual.Processador.ProcessarAsync(corpoCerto, CancellationToken.None)).Resultado);
        var guardada = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(execucaoId, guardada.ExecucaoId);

        // execucaoId divergente do que o Execution já informou
        var outraExecucao = Guid.NewGuid();
        var concluidaOutra = await ReceberAsync(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId, null, null, ("execucaoId", outraExecucao));
        Assert.Equal(ResultadoMensagem.Rejeitada, concluidaOutra.Resultado);
        Assert.Equal(EstadoSaga.IntervencaoRequerida, (await SagaAsync(s.OrdemServicoId)).Estado);
        Assert.Equal($"execucaoId {outraExecucao} diverge do da Saga ({execucaoId}).", Atual.Alertas.Alertas[1].Mensagem);

        await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "desistência"));
        var recusaOutra = await ReceberAsync(TiposMensagem.ExecucaoCancelada, s.OrdemServicoId, null, null, ("execucaoId", outraExecucao));
        Assert.Equal(ResultadoMensagem.Rejeitada, recusaOutra.Resultado);
        Assert.Equal(SituacaoEtapa.Compensando, (await SagaAsync(s.OrdemServicoId)).Etapa(2).Situacao);
        Assert.Equal("saga.mensagem-divergente", Atual.Alertas.Alertas[2].Codigo);
        Assert.Equal(ResultadoMensagem.Aplicada, (await ReceberAsync(TiposMensagem.ExecucaoCancelada, s.OrdemServicoId, null, null, ("execucaoId", execucaoId))).Resultado);

        // orcamentoId divergente na compensação da etapa 1
        var outroOrcamento = Guid.NewGuid();
        var orcamentoOutro = await ReceberAsync(TiposMensagem.OrcamentoCancelado, s.OrdemServicoId, null, null, ("orcamentoId", outroOrcamento));
        Assert.Equal(ResultadoMensagem.Rejeitada, orcamentoOutro.Resultado);
        Assert.Equal(EstadoSaga.Compensando, (await SagaAsync(s.OrdemServicoId)).Estado);
        Assert.Equal($"orcamentoId {outroOrcamento} diverge do da Saga ({s.OrcamentoId}).", Atual.Alertas.Alertas[3].Mensagem);
        var recusaOrcamentoOutro = await ReceberAsync(TiposMensagem.OrcamentoCancelamentoRecusado, s.OrdemServicoId, null, null, ("orcamentoId", outroOrcamento), ("motivo", "x"));
        Assert.Equal(ResultadoMensagem.Rejeitada, recusaOrcamentoOutro.Resultado);
        Assert.Equal(EstadoSaga.Compensando, (await SagaAsync(s.OrdemServicoId)).Estado);
        Assert.Equal(5, Atual.Alertas.Alertas.Count);
        Assert.All(Atual.Alertas.Alertas, a => Assert.Equal("saga.mensagem-divergente", a.Codigo));

        var (corpoOrcamento, _) = Mensagem(TiposMensagem.OrcamentoCancelado, s.OrdemServicoId, s.CorrelacaoId, null, ("orcamentoId", s.OrcamentoId));
        Assert.Equal(ResultadoMensagem.Aplicada, (await Atual.Processador.ProcessarAsync(corpoOrcamento, CancellationToken.None)).Resultado);
        Assert.Equal(EstadoSaga.Compensada, (await SagaAsync(s.OrdemServicoId)).Estado);
    }

    [Fact]
    public async Task CompensatesEachFailedStage_RefusalReasonIsLimitedTo2000CharactersInTheCompensationCommand()
    {
        var s = await IniciarSagaAsync();
        var motivo = new string('x', 2000);

        await ReceberAsync(TiposMensagem.ExecucaoLiberacaoRecusada, s.OrdemServicoId, null, null, ("motivo", motivo));

        var esperado = ($"Liberação recusada pelo Execution: {motivo}.")[..1999] + "…";
        Assert.Equal(2000, esperado.Length);
        var comando = Json.Ler(Atual.Envio.Enviados[1].Corpo);
        Assert.Equal("billing.orcamento.cancelar.v1", comando.GetProperty("tipo").GetString());
        Assert.Equal(esperado, comando.GetProperty("motivo").GetString());
        Assert.Equal(esperado, (await SagaAsync(s.OrdemServicoId)).MotivoCompensacao);
    }

    [Fact]
    public async Task CompensatesEachFailedStage_ExceptionInOneSagaDoesNotAbortTheDispatcherCycleForTheOthers()
    {
        Atual.Envio.Falha = (_, _) => new IOException("SQS fora do ar");
        var a = await IniciarSagaAsync();
        Relogio.Avancar(TimeSpan.FromSeconds(1));
        var b = await IniciarSagaAsync();
        Relogio.Avancar(TimeSpan.FromSeconds(1));
        var c = await IniciarSagaAsync();
        Relogio.Avancar(TimeSpan.FromSeconds(10));
        SagaRepositorioInstavel? instavel = null;
        var reiniciado = NovosComponentes(r => instavel = new SagaRepositorioInstavel(r));
        instavel!.LeituraComFalha.Add(a.OrdemServicoId); // a é a primeira da fila do índice: as demais vêm depois dela

        var visitadas = await reiniciado.Despachador.ExecutarCicloAsync(CancellationToken.None);

        Assert.Equal(3, visitadas);
        Assert.Equal(2, reiniciado.Envio.Enviados.Count);
        Assert.Equal(new[] { b.OrdemServicoId, c.OrdemServicoId }.Order(), reiniciado.Envio.Enviados.Select(e => Json.Ler(e.Corpo).GetProperty("ordemServicoId").GetGuid()).Order());
        var alerta = Assert.Single(reiniciado.Alertas.Alertas);
        Assert.Equal("saga.despacho-falhou", alerta.Codigo);
        Assert.Equal(a.OrdemServicoId, alerta.OrdemServicoId);
        Assert.Equal($"Falha ao progredir a Saga da OS {a.OrdemServicoId}: Documento da Saga ilegível (falha injetada). As demais Sagas seguem; o trabalho permanece persistido e será retomado no próximo ciclo.", alerta.Mensagem);
        Assert.True(Assert.Single((await SagaAsync(a.OrdemServicoId)).Comandos).Pendente);
        foreach (var x in new[] { b, c }) Assert.False(Assert.Single((await SagaAsync(x.OrdemServicoId)).Comandos).Pendente);
    }

    // ---- C5: pagamento capturado e falha de sincronização ----

    [Fact]
    public async Task CapturedPaymentForwardRecovery_RetriesWithBackoffAlertsAndNeverRefunds()
    {
        var s = await IniciarSagaAsync();
        await ReceberAsync(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId);
        Controle.AplicaA = status => status == "Paga";
        Controle.FalhasRestantes = 3;
        Controle.Chamadas.Clear();
        var enviosAntes = Atual.Envio.Tentativas.Count;

        var resultado = await PagamentoAsync(s.OrdemServicoId, "Capturado", "pix-123");

        Assert.Equal(ResultadoMensagem.Aplicada, resultado.Resultado);
        var apos1 = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.PagamentoCapturado, apos1.Estado);
        Assert.Equal(["Paga"], apos1.SincronizacoesPendentes);
        Assert.Equal(1, apos1.TentativasSincronizacao);
        Assert.Equal(T0.AddSeconds(1.25), apos1.ProximaSincronizacaoEm);
        Assert.Contains("falha injetada", apos1.UltimoErroSincronizacao);
        Assert.Equal("Concluida", (await OSAsync(s.OrdemServicoId)).Status);
        var primeiro = Assert.Single(Atual.Alertas.Alertas);
        Assert.Equal("saga.sincronizacao-os-falhou", primeiro.Codigo);
        Assert.Equal(s.OrdemServicoId, primeiro.OrdemServicoId);
        Assert.Contains("tentativa 1", primeiro.Mensagem);
        Assert.Contains("nova tentativa em 1.25s", primeiro.Mensagem);
        Assert.Contains("nenhum estorno ou desfazimento de pagamento será executado", primeiro.Mensagem);

        // Antes do prazo o despachador não retenta.
        Assert.Equal(0, await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None));
        Assert.Single(Controle.Chamadas);

        Relogio.Avancar(TimeSpan.FromSeconds(1.25));
        Assert.Equal(1, await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None));
        var apos2 = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(2, apos2.TentativasSincronizacao);
        Assert.Equal(T0.AddSeconds(1.25 + 2.5), apos2.ProximaSincronizacaoEm);

        Relogio.Avancar(TimeSpan.FromSeconds(2.5));
        await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None);
        var apos3 = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(3, apos3.TentativasSincronizacao);
        Assert.Equal(T0.AddSeconds(1.25 + 2.5 + 5), apos3.ProximaSincronizacaoEm);
        Assert.Equal(EstadoSaga.PagamentoCapturado, apos3.Estado);

        Relogio.Avancar(TimeSpan.FromSeconds(5));
        await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None);

        var final = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.PagamentoCapturado, final.Estado);
        Assert.Empty(final.SincronizacoesPendentes);
        Assert.Equal(0, final.TentativasSincronizacao);
        Assert.Null(final.UltimoErroSincronizacao);
        Assert.Equal(["Paga", "Paga", "Paga", "Paga"], Controle.Chamadas);
        Assert.Equal(["saga.sincronizacao-os-falhou", "saga.sincronizacao-os-falhou", "saga.sincronizacao-os-falhou"], Atual.Alertas.Codigos);
        Assert.Equal("Paga", (await OSAsync(s.OrdemServicoId)).Status);
        // Nenhum estorno, reembolso ou qualquer outro envio depois da captura.
        Assert.Equal(enviosAntes, Atual.Envio.Tentativas.Count);
        Assert.DoesNotContain(Atual.Envio.Enviados, e => e.Destino == DestinoComando.Billing);
        Assert.Empty(Atual.Envio.Dlq);
    }

    [Fact]
    public async Task CapturedPaymentForwardRecovery_RetryDelayIsCappedAndNeverGivesUp()
    {
        var s = await IniciarSagaAsync();
        await ReceberAsync(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId);
        Controle.AplicaA = status => status == "Paga";
        Controle.FalhasRestantes = 12;
        await PagamentoAsync(s.OrdemServicoId, "Capturado");

        var atrasos = new List<double>();
        for (var i = 0; i < 12; i++)
        {
            var saga = await SagaAsync(s.OrdemServicoId);
            var atraso = saga.ProximaSincronizacaoEm!.Value - saga.AtualizadoEm;
            atrasos.Add(atraso.TotalSeconds);
            Relogio.Avancar(atraso);
            await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None);
        }

        // base 1s dobrando até o teto de 300s, mais 25% de jitter (aleatório fixo em 0,5 e fator 0,5).
        Assert.Equal([1.25, 2.5, 5, 10, 20, 40, 80, 160, 320, 375, 375, 375], atrasos);
        var final = await SagaAsync(s.OrdemServicoId);
        Assert.Empty(final.SincronizacoesPendentes);
        Assert.Equal(12, Atual.Alertas.Alertas.Count(a => a.Codigo == "saga.sincronizacao-os-falhou"));
        Assert.Equal("Paga", (await OSAsync(s.OrdemServicoId)).Status);
        Assert.Single(Atual.Envio.Tentativas);
    }
}
