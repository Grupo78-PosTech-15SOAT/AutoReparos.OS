using Amazon.DynamoDBv2.Model;
using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;

/// <summary>C1 a C3: persistir antes de publicar, retomar após reinício e deduplicar por eventId (DynamoDB Local e PostgreSQL reais).</summary>
public sealed class SagaPersistenceTests(SagaAmbiente ambiente) : SagaTestBase(ambiente)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SagaPersistsBeforePublish_StateCorrelationAndPendingCommandAreStoredBeforeTheFirstSend()
    {
        var (id, clienteId, veiculoId) = await CriarOSAsync();
        var orcamentoId = Guid.NewGuid();
        var correlacao = Guid.NewGuid();
        Dictionary<string, AttributeValue>? itemNoEnvio = null;
        Atual.Envio.AoEnviar = async (_, _) => itemNoEnvio = await ItemBrutoAsync(id);

        // O clienteId da mensagem é de propósito diferente: o comando usa o da OS no Postgres.
        var (corpo, eventId) = Mensagem(TiposMensagem.OrcamentoAprovado, id, correlacao, null, ("orcamentoId", orcamentoId), ("clienteId", Guid.NewGuid()));
        var resultado = await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None);

        Assert.Equal(new ResultadoProcessamento(DesfechoMensagem.Processada, ResultadoMensagem.Aplicada, 1), resultado);
        var enviado = Assert.Single(Atual.Envio.Enviados);
        Assert.Equal(DestinoComando.Execucao, enviado.Destino);

        // No instante do envio o estado, a versão e o comando pendente já estavam gravados.
        Assert.NotNull(itemNoEnvio);
        Assert.Equal("ExecucaoLiberada", itemNoEnvio["estado"].S);
        Assert.Equal("3", itemNoEnvio["versao"].N);
        Assert.Equal(correlacao.ToString(), itemNoEnvio["correlacaoId"].S);
        Assert.Equal("S", itemNoEnvio["trabalhoPendente"].S);
        var documento = Json.Ler(itemNoEnvio["documento"].S);
        var comandoGravado = Assert.Single(documento.GetProperty("comandos").EnumerateArray());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, comandoGravado.GetProperty("enviadoEm").ValueKind);
        Assert.Equal(enviado.Corpo, comandoGravado.GetProperty("corpo").GetString());
        Assert.Equal("ordem-servico.liberada.v1", comandoGravado.GetProperty("tipo").GetString());

        Assert.Equal(["clienteId", "correlacaoId", "eventId", "id", "ocorreuEm", "ordemServicoId", "tipo", "veiculoId"], Json.Chaves(enviado.Corpo));
        var mensagem = Json.Ler(enviado.Corpo);
        Assert.Equal("ordem-servico.liberada.v1", mensagem.GetProperty("tipo").GetString());
        Assert.Equal(id, mensagem.GetProperty("id").GetGuid());
        Assert.Equal(id, mensagem.GetProperty("ordemServicoId").GetGuid());
        Assert.Equal(clienteId, mensagem.GetProperty("clienteId").GetGuid());
        Assert.Equal(veiculoId, mensagem.GetProperty("veiculoId").GetGuid());
        Assert.Equal(correlacao, mensagem.GetProperty("correlacaoId").GetGuid());
        Assert.Equal("2026-11-10T12:00:00Z", mensagem.GetProperty("ocorreuEm").GetString());
        Assert.NotEqual(eventId, mensagem.GetProperty("eventId").GetGuid());

        var saga = await SagaAsync(id);
        Assert.Equal(4, saga.Versao);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, saga.Estado);
        Assert.Equal(T0, Assert.Single(saga.Comandos).EnviadoEm);
        Assert.Equal(["OS sincronizada para OrcamentoAprovado.", "OS sincronizada para EmExecucao."], Sincronizacoes(saga));
        await AssertOSAsync(id, "EmExecucao", "EmExecucao", "OrcamentoAprovado", "Recebida");
        Assert.True(await Atual.Sagas.EventoProcessadoAsync(eventId, CancellationToken.None));
        Assert.Empty(Atual.Alertas.Alertas);
    }

    [Fact]
    public async Task SagaPersistsBeforePublish_EveryCommandOfTheCancellationFlowIsStoredBeforeItIsSent()
    {
        var (id, _, _) = await CriarOSAsync();
        var correlacao = Guid.NewGuid();
        var orcamentoId = Guid.NewGuid();
        var capturas = new List<(string Tipo, string Corpo, Dictionary<string, AttributeValue> Item)>();
        Atual.Envio.AoEnviar = async (_, corpo) => capturas.Add((Json.Tipo(corpo), corpo, await ItemBrutoAsync(id)));

        var (aprovado, _) = Mensagem(TiposMensagem.OrcamentoAprovado, id, correlacao, null, ("orcamentoId", orcamentoId), ("clienteId", Guid.NewGuid()));
        await Atual.Processador.ProcessarAsync(aprovado, CancellationToken.None);
        await ReceberAsync(TiposMensagem.OrdemServicoCancelar, id, null, null, ("motivo", "cliente desistiu"));
        await ReceberAsync(TiposMensagem.ExecucaoCancelada, id, null, null, ("execucaoId", Guid.NewGuid()));

        Assert.Equal(["ordem-servico.liberada.v1", "execucao.cancelar.v1", "billing.orcamento.cancelar.v1"], capturas.Select(c => c.Tipo));
        var estados = new[] { "ExecucaoLiberada", "Compensando", "Compensando" };
        long versaoAnterior = 0;
        for (var i = 0; i < capturas.Count; i++)
        {
            var (tipo, corpo, item) = capturas[i];
            // No instante do envio de cada comando, o estado, o correlacaoId e o próprio comando (ainda não enviado) já estavam gravados.
            Assert.Equal(estados[i], item["estado"].S);
            Assert.Equal(correlacao.ToString(), item["correlacaoId"].S);
            Assert.Equal("S", item["trabalhoPendente"].S);
            var versao = long.Parse(item["versao"].N);
            Assert.True(versao > versaoAnterior, $"a versão gravada deve crescer a cada comando ({tipo}: {versao} <= {versaoAnterior})");
            versaoAnterior = versao;
            var comandoGravado = Assert.Single(Json.Ler(item["documento"].S).GetProperty("comandos").EnumerateArray(), c => c.GetProperty("tipo").GetString() == tipo);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, comandoGravado.GetProperty("enviadoEm").ValueKind);
            Assert.Equal(corpo, comandoGravado.GetProperty("corpo").GetString());
        }
        Assert.Equal(["ordem-servico.liberada.v1", "execucao.cancelar.v1", "billing.orcamento.cancelar.v1"], (await SagaAsync(id)).Comandos.Select(c => c.Tipo));
        Assert.All((await SagaAsync(id)).Comandos, c => Assert.NotNull(c.EnviadoEm));
    }

    [Fact]
    public async Task SagaPersistsBeforePublish_FailedSendLeavesTheCommandPersistedWithItsRetrySchedule()
    {
        Atual.Envio.Falha = (_, _) => new IOException("SQS fora do ar");

        var s = await IniciarSagaAsync();

        Assert.Empty(Atual.Envio.Enviados);
        Assert.Single(Atual.Envio.Tentativas);
        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(4, saga.Versao);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, saga.Estado);
        var comando = Assert.Single(saga.Comandos);
        Assert.True(comando.Pendente);
        Assert.Equal(1, comando.Tentativas);
        Assert.Equal(T0.AddSeconds(1.25), comando.ProximaTentativaEm);
        Assert.Equal("SQS fora do ar", comando.UltimoErro);
        var item = await ItemBrutoAsync(s.OrdemServicoId);
        Assert.Equal("S", item["trabalhoPendente"].S);
        Assert.Equal("2026-11-10T12:00:01.2500000Z", item["proximaTentativaEm"].S);
        Assert.Equal(s.CorrelacaoId.ToString(), item["correlacaoId"].S);
    }

    [Fact]
    public async Task SagaPersistsBeforePublish_CreationRecordsStagesAndGeneratesCorrelationWhenAbsent()
    {
        var (id, _, _) = await CriarOSAsync();
        var orcamentoId = Guid.NewGuid();
        var (corpo, eventId) = Mensagem(TiposMensagem.OrcamentoAprovado, id, null, null, ("orcamentoId", orcamentoId), ("clienteId", Guid.NewGuid()));

        await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None);

        var saga = await SagaAsync(id);
        Assert.NotEqual(Guid.Empty, saga.CorrelacaoId);
        Assert.Equal(orcamentoId, saga.OrcamentoId);
        Assert.Equal([SituacaoEtapa.Concluida, SituacaoEtapa.Concluida, SituacaoEtapa.Pendente, SituacaoEtapa.Pendente], saga.Etapas.Select(e => e.Situacao));
        Assert.Equal(["billing.orcamento.cancelar.v1", "execucao.cancelar.v1", null, null], saga.Etapas.Select(e => e.Compensacao));
        Assert.Equal(saga.CorrelacaoId, Json.Ler(Atual.Envio.Enviados.Single().Corpo).GetProperty("correlacaoId").GetGuid());
        var evento = await Dynamo.GetItemAsync(TabelasDynamo.TabelaEventos, new Dictionary<string, AttributeValue> { ["eventId"] = new() { S = eventId.ToString() } });
        Assert.Equal("orcamento.aprovado.v1", evento.Item["tipo"].S);
        Assert.Equal(id.ToString(), evento.Item["ordemServicoId"].S);
    }

    [Fact]
    public async Task SagaPersistsBeforePublish_ConditionalWriteRejectsAStaleVersion()
    {
        var s = await IniciarSagaAsync();
        var copiaA = await SagaAsync(s.OrdemServicoId);
        var copiaB = await SagaAsync(s.OrdemServicoId);
        copiaA.AplicarExecucaoConcluida(Guid.NewGuid(), T0);
        copiaB.AplicarExecucaoFalhou(Guid.NewGuid(), "x", T0);

        Assert.Equal(ResultadoGravacao.Gravada, await Atual.Sagas.GravarAsync(copiaA, null, CancellationToken.None));
        Assert.Equal(5, copiaA.Versao);
        Assert.Equal(ResultadoGravacao.ConflitoVersao, await Atual.Sagas.GravarAsync(copiaB, null, CancellationToken.None));
        Assert.Equal(4, copiaB.Versao);
        var persistida = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(5, persistida.Versao);
        Assert.Equal(EstadoSaga.ExecucaoConcluida, persistida.Estado);
        // Criar de novo uma Saga já existente também conflita.
        var nova = SagaOS.Iniciar(s.OrdemServicoId, Guid.NewGuid(), null, Guid.NewGuid(), T0);
        Assert.Equal(ResultadoGravacao.ConflitoVersao, await Atual.Sagas.GravarAsync(nova, null, CancellationToken.None));
    }

    [Fact]
    public async Task SagaResumeAfterRestart_PendingCommandIsPublishedByTheNewProcess()
    {
        Atual.Envio.Falha = (_, _) => new IOException("SQS fora do ar");
        var s = await IniciarSagaAsync();
        var corpoOriginal = Assert.Single(Atual.Envio.Tentativas).Corpo;
        Relogio.Avancar(TimeSpan.FromSeconds(2));

        var reiniciado = NovosComponentes();
        var visitadas = await reiniciado.Despachador.ExecutarCicloAsync(CancellationToken.None);

        Assert.Equal(1, visitadas);
        var enviado = Assert.Single(reiniciado.Envio.Enviados);
        Assert.Equal(DestinoComando.Execucao, enviado.Destino);
        Assert.Equal(corpoOriginal, enviado.Corpo);
        var saga = await SagaAsync(s.OrdemServicoId, reiniciado);
        Assert.Equal(5, saga.Versao);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, saga.Estado);
        Assert.Equal(T0.AddSeconds(2), Assert.Single(saga.Comandos).EnviadoEm);
        Assert.Equal([SituacaoEtapa.Concluida, SituacaoEtapa.Concluida, SituacaoEtapa.Pendente, SituacaoEtapa.Pendente], saga.Etapas.Select(e => e.Situacao));
        Assert.Equal(0, await reiniciado.Despachador.ExecutarCicloAsync(CancellationToken.None));
        Assert.Single(reiniciado.Envio.Enviados);
    }

    [Fact]
    public async Task SagaResumeAfterRestart_CompletedStagesAreKeptAndTheFlowContinues()
    {
        var s = await IniciarSagaAsync();
        await ReceberAsync(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId);
        Assert.Equal(EstadoSaga.ExecucaoConcluida, (await SagaAsync(s.OrdemServicoId)).Estado);

        var reiniciado = NovosComponentes();
        var persistida = await SagaAsync(s.OrdemServicoId, reiniciado);
        Assert.Equal(EstadoSaga.ExecucaoConcluida, persistida.Estado);
        Assert.Equal(s.CorrelacaoId, persistida.CorrelacaoId);
        Assert.Equal([SituacaoEtapa.Concluida, SituacaoEtapa.Concluida, SituacaoEtapa.Concluida, SituacaoEtapa.Pendente], persistida.Etapas.Select(e => e.Situacao));

        var resultado = await ReceberAsync(TiposMensagem.PagamentoResultado, s.OrdemServicoId, reiniciado, null, ("pagamentoId", Guid.NewGuid()), ("status", "Capturado"), ("referencia", "pix-1"));

        Assert.Equal(ResultadoMensagem.Aplicada, resultado.Resultado);
        var final = await SagaAsync(s.OrdemServicoId, reiniciado);
        Assert.Equal(EstadoSaga.PagamentoCapturado, final.Estado);
        Assert.Equal([SituacaoEtapa.Concluida, SituacaoEtapa.Concluida, SituacaoEtapa.Concluida, SituacaoEtapa.Concluida], final.Etapas.Select(e => e.Situacao));
        await AssertOSAsync(s.OrdemServicoId, "Paga", "Concluida", "EmExecucao", "OrcamentoAprovado", "Paga", "Recebida");
    }

    [Fact]
    public async Task SagaResumeAfterRestart_PendingOsSynchronizationSurvivesAndIsRetriedByTheNewProcess()
    {
        var s = await IniciarSagaAsync();
        await ReceberAsync(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId);
        Controle.AplicaA = status => status == "Paga";
        Controle.FalhasRestantes = 1;
        await ReceberAsync(TiposMensagem.PagamentoResultado, s.OrdemServicoId, null, null, ("pagamentoId", Guid.NewGuid()), ("status", "Capturado"));
        Assert.Equal(["Paga"], (await SagaAsync(s.OrdemServicoId)).SincronizacoesPendentes);
        Assert.Equal("Concluida", (await OSAsync(s.OrdemServicoId)).Status);
        Relogio.Avancar(TimeSpan.FromSeconds(2));

        var reiniciado = NovosComponentes();
        await reiniciado.Despachador.ExecutarCicloAsync(CancellationToken.None);

        var saga = await SagaAsync(s.OrdemServicoId, reiniciado);
        Assert.Equal(EstadoSaga.PagamentoCapturado, saga.Estado);
        Assert.Empty(saga.SincronizacoesPendentes);
        Assert.Equal("Paga", (await OSAsync(s.OrdemServicoId)).Status);
        Assert.Empty(reiniciado.Alertas.Alertas);
    }

    [Fact]
    public async Task SagaDuplicateEvent_SequentialRedeliveryHasNoSecondEffect()
    {
        var s = await IniciarSagaAsync();
        var versaoAntes = (await SagaAsync(s.OrdemServicoId)).Versao;
        var (corpo, _) = Mensagem(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId);

        var primeira = await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None);
        var segunda = await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None);

        Assert.Equal(ResultadoMensagem.Aplicada, primeira.Resultado);
        Assert.Equal(new ResultadoProcessamento(DesfechoMensagem.Processada, ResultadoMensagem.Duplicada, 1), segunda);
        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(versaoAntes + 2, saga.Versao); // transição + sincronização; a duplicata não grava nada
        Assert.Equal(EstadoSaga.ExecucaoConcluida, saga.Estado);
        Assert.Single(Atual.Envio.Enviados);
        await AssertOSAsync(s.OrdemServicoId, "Concluida", "Concluida", "EmExecucao", "OrcamentoAprovado", "Recebida");
        Assert.Equal(3, await EventosDeOSAsync(s.OrdemServicoId));
    }

    [Fact]
    public async Task SagaDuplicateEvent_RedeliveredBudgetApprovalDoesNotCreateASecondSagaOrCommand()
    {
        var (id, _, _) = await CriarOSAsync();
        var (corpo, _) = Mensagem(TiposMensagem.OrcamentoAprovado, id, Guid.NewGuid(), null, ("orcamentoId", Guid.NewGuid()), ("clienteId", Guid.NewGuid()));

        await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None);
        var repetida = await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None);

        Assert.Equal(ResultadoMensagem.Duplicada, repetida.Resultado);
        Assert.Equal(4, (await SagaAsync(id)).Versao);
        Assert.Single(Atual.Envio.Enviados);
        Assert.Single(Atual.Envio.Tentativas);
    }

    [Fact]
    public async Task SagaDuplicateEvent_EightConcurrentConsumersOfTheSameEventProduceASingleEffect()
    {
        var s = await IniciarSagaAsync();
        var (corpo, eventId) = Mensagem(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId);
        var consumidores = Enumerable.Range(0, 8).Select(_ => NovosComponentes()).ToList();

        var resultados = await Task.WhenAll(consumidores.Select(c => Task.Run(() => c.Processador.ProcessarAsync(corpo, CancellationToken.None))));

        Assert.Equal(1, resultados.Count(r => r.Resultado == ResultadoMensagem.Aplicada));
        Assert.Equal(7, resultados.Count(r => r.Resultado == ResultadoMensagem.Duplicada));
        Assert.All(resultados, r => Assert.Equal(DesfechoMensagem.Processada, r.Desfecho));
        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.ExecucaoConcluida, saga.Estado);
        Assert.Equal(6, saga.Versao);
        Assert.Equal(["OS sincronizada para OrcamentoAprovado.", "OS sincronizada para EmExecucao.", "OS sincronizada para Concluida."], Sincronizacoes(saga));
        Assert.Single(saga.Registros, r => r.EventId == eventId && r.Evento == TiposMensagem.ExecucaoConcluida);
        await AssertOSAsync(s.OrdemServicoId, "Concluida", "Concluida", "EmExecucao", "OrcamentoAprovado", "Recebida");
        Assert.Equal(3, await EventosDeOSAsync(s.OrdemServicoId));
    }

    [Fact]
    public async Task SagaDuplicateEvent_EightConcurrentConsumersOfTheSameBudgetApprovalCreateOneSagaAndPublishOneCommand()
    {
        var (id, _, _) = await CriarOSAsync();
        var (corpo, _) = Mensagem(TiposMensagem.OrcamentoAprovado, id, Guid.NewGuid(), null, ("orcamentoId", Guid.NewGuid()), ("clienteId", Guid.NewGuid()));
        var consumidores = Enumerable.Range(0, 8).Select(_ => NovosComponentes()).ToList();

        var resultados = await Task.WhenAll(consumidores.Select(c => Task.Run(() => c.Processador.ProcessarAsync(corpo, CancellationToken.None))));

        Assert.Equal(1, resultados.Count(r => r.Resultado == ResultadoMensagem.Aplicada));
        Assert.Equal(7, resultados.Count(r => r.Resultado == ResultadoMensagem.Duplicada));
        Assert.Equal(1, consumidores.Sum(c => c.Envio.Enviados.Count));
        var saga = await SagaAsync(id);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, saga.Estado);
        Assert.Equal(4, saga.Versao);
        await AssertOSAsync(id, "EmExecucao", "EmExecucao", "OrcamentoAprovado", "Recebida");
    }

    [Theory]
    [InlineData("execucao.concluida.v1", "Transição inválida a partir de ExecucaoConcluida.")]
    [InlineData("execucao.falhou.v1", "Transição inválida a partir de ExecucaoConcluida.")]
    [InlineData("execucao.liberacao-recusada.v1", "Transição inválida a partir de ExecucaoConcluida.")]
    [InlineData("orcamento.cancelado.v1", "Confirmação inesperada da etapa 1 em ExecucaoConcluida/Concluida.")]
    [InlineData("execucao.cancelada.v1", "Confirmação inesperada da etapa 2 em ExecucaoConcluida/Concluida.")]
    [InlineData("orcamento.cancelamento-recusado.v1", "Recusa inesperada da etapa 1 em ExecucaoConcluida/Concluida.")]
    [InlineData("execucao.cancelamento-recusado.v1", "Recusa inesperada da etapa 2 em ExecucaoConcluida/Concluida.")]
    public async Task SagaDuplicateEvent_OutOfOrderTransitionIsRejectedOnceAndIdempotently(string tipo, string motivo)
    {
        var s = await IniciarSagaAsync();
        await ReceberAsync(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId);
        var antes = await SagaAsync(s.OrdemServicoId);
        var eventId = Guid.NewGuid();

        var primeira = await ReceberAsync(tipo, s.OrdemServicoId, null, eventId, ("motivo", "x"));
        var repetida = await ReceberAsync(tipo, s.OrdemServicoId, null, eventId, ("motivo", "x"));

        Assert.Equal(ResultadoMensagem.Rejeitada, primeira.Resultado);
        Assert.Equal(ResultadoMensagem.Duplicada, repetida.Resultado);
        var depois = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(antes.Versao + 1, depois.Versao);
        Assert.Equal(EstadoSaga.ExecucaoConcluida, depois.Estado);
        Assert.Equal(antes.Etapas.Select(e => e.Situacao), depois.Etapas.Select(e => e.Situacao));
        Assert.Equal($"Transição rejeitada: {motivo}", depois.Registros[^1].Descricao);
        Assert.Equal(eventId, depois.Registros[^1].EventId);
        Assert.Empty(Atual.Alertas.Alertas);
        Assert.Single(Atual.Envio.Enviados);
    }

    [Fact]
    public async Task SagaDuplicateEvent_AnotherApprovalForAnExistingSagaIsRejectedWithoutChangingState()
    {
        var s = await IniciarSagaAsync();
        var antes = await SagaAsync(s.OrdemServicoId);

        var resultado = await ReceberAsync(TiposMensagem.OrcamentoAprovado, s.OrdemServicoId, null, null, ("orcamentoId", Guid.NewGuid()), ("clienteId", Guid.NewGuid()));

        Assert.Equal(ResultadoMensagem.Rejeitada, resultado.Resultado);
        var depois = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(antes.Versao + 1, depois.Versao);
        Assert.Equal(s.OrcamentoId, depois.OrcamentoId);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, depois.Estado);
        Assert.Single(Atual.Envio.Enviados);
    }
}
