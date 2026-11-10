using Amazon.DynamoDBv2.Model;
using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;

/// <summary>C6: retry limitado com backoff exponencial e jitter, DLQ e alerta; mensagens malformadas.</summary>
public sealed class SagaRetryTests(SagaAmbiente ambiente) : SagaTestBase(ambiente)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan[] AtrasosEsperados = [TimeSpan.FromSeconds(1.25), TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    [Fact]
    public async Task RetryExhaustionDeadLetter_TransientFailureIsRetriedFiveTimesThenGoesToTheDeadLetterQueue()
    {
        var ordemSemSaga = Guid.NewGuid();
        var (corpo, eventId) = Mensagem(TiposMensagem.ExecucaoConcluida, ordemSemSaga, null, null, ("execucaoId", Guid.NewGuid()));

        var resultado = await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None);

        Assert.Equal(new ResultadoProcessamento(DesfechoMensagem.EnviadaParaDlq, null, 5), resultado);
        // base 1s dobrando, mais 25% de jitter (aleatório fixo em 0,5 e fator 0,5): quatro esperas entre cinco tentativas.
        Assert.Equal(AtrasosEsperados, Esperas);
        var dlq = Assert.Single(Atual.Envio.Dlq);
        Assert.Equal(corpo, dlq.Corpo);
        Assert.Equal($"Falha transitória esgotada após 5 tentativas ao processar execucao.concluida.v1: Não existe Saga para a OS {ordemSemSaga}; a mensagem execucao.concluida.v1 chegou antes de orcamento.aprovado.v1.", dlq.Motivo);
        var alerta = Assert.Single(Atual.Alertas.Alertas);
        Assert.Equal("saga.mensagem-esgotada", alerta.Codigo);
        Assert.Equal(ordemSemSaga, alerta.OrdemServicoId);
        Assert.Equal(eventId, alerta.EventId);
        Assert.EndsWith("Mensagem enviada à DLQ; intervenção necessária.", alerta.Mensagem, StringComparison.Ordinal);
        Assert.Null(await Atual.Sagas.ObterAsync(ordemSemSaga, CancellationToken.None));
        Assert.False(await Atual.Sagas.EventoProcessadoAsync(eventId, CancellationToken.None));
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public async Task RetryExhaustionDeadLetter_FifthAttemptIsTheLastBeforeTheDeadLetterQueue(int falhas, bool vaiParaDlq)
    {
        SagaRepositorioInstavel? instavel = null;
        var componentes = NovosComponentes(r => instavel = new SagaRepositorioInstavel(r) { FalhasRestantes = falhas });
        var (id, _, _) = await CriarOSAsync();
        var (corpo, _) = Mensagem(TiposMensagem.OrcamentoAprovado, id, Guid.NewGuid(), null, ("orcamentoId", Guid.NewGuid()), ("clienteId", Guid.NewGuid()));

        var resultado = await componentes.Processador.ProcessarAsync(corpo, CancellationToken.None);

        Assert.Equal(5, instavel!.Chamadas);
        Assert.Equal(5, resultado.Tentativas);
        Assert.Equal(AtrasosEsperados, Esperas);
        if (vaiParaDlq)
        {
            Assert.Equal(DesfechoMensagem.EnviadaParaDlq, resultado.Desfecho);
            Assert.Single(componentes.Envio.Dlq);
            Assert.Equal(["saga.mensagem-esgotada"], componentes.Alertas.Codigos);
            Assert.Null(await componentes.Sagas.ObterAsync(id, CancellationToken.None));
        }
        else
        {
            Assert.Equal(new ResultadoProcessamento(DesfechoMensagem.Processada, ResultadoMensagem.Aplicada, 5), resultado);
            Assert.Empty(componentes.Envio.Dlq);
            Assert.Empty(componentes.Alertas.Alertas);
            Assert.Equal(EstadoSaga.ExecucaoLiberada, (await SagaAsync(id, componentes)).Estado);
        }
    }

    [Theory]
    [InlineData("isto não é json", "JSON inválido")]
    [InlineData("[]", "A mensagem deve ser um objeto JSON.")]
    [InlineData("", "Mensagem vazia.")]
    [InlineData("{\"tipo\":\"orcamento.aprovado.v1\",\"ordemServicoId\":\"11111111-1111-1111-1111-111111111111\"}", "eventId ausente ou inválido.")]
    [InlineData("{\"eventId\":\"22222222-2222-2222-2222-222222222222\",\"ordemServicoId\":\"11111111-1111-1111-1111-111111111111\"}", "tipo ausente ou inválido.")]
    [InlineData("{\"eventId\":\"22222222-2222-2222-2222-222222222222\",\"tipo\":\"fatura.emitida.v1\",\"ordemServicoId\":\"11111111-1111-1111-1111-111111111111\"}", "tipo desconhecido: fatura.emitida.v1.")]
    [InlineData("{\"eventId\":\"22222222-2222-2222-2222-222222222222\",\"tipo\":\"execucao.concluida.v1\",\"ordemServicoId\":\"nao-e-guid\"}", "ordemServicoId ausente ou inválido.")]
    [InlineData("{\"eventId\":\"22222222-2222-2222-2222-222222222222\",\"tipo\":\"execucao.concluida.v1\"}", "ordemServicoId ausente ou inválido.")]
    [InlineData("{\"eventId\":\"22222222-2222-2222-2222-222222222222\",\"tipo\":\"orcamento.aprovado.v1\",\"ordemServicoId\":\"11111111-1111-1111-1111-111111111111\",\"clienteId\":\"33333333-3333-3333-3333-333333333333\"}", "orcamentoId ausente ou inválido.")]
    [InlineData("{\"eventId\":\"22222222-2222-2222-2222-222222222222\",\"tipo\":\"pagamento.resultado.v1\",\"ordemServicoId\":\"11111111-1111-1111-1111-111111111111\",\"pagamentoId\":\"33333333-3333-3333-3333-333333333333\",\"status\":\"Estornado\"}", "status de pagamento inválido: Estornado.")]
    [InlineData("{\"eventId\":\"22222222-2222-2222-2222-222222222222\",\"tipo\":\"execucao.concluida.v1\",\"ordemServicoId\":\"11111111-1111-1111-1111-111111111111\",\"ocorreuEm\":\"ontem\"}", "ocorreuEm inválido.")]
    [InlineData("{\"eventId\":\"22222222-2222-2222-2222-222222222222\",\"tipo\":\"pagamento.resultado.v1\",\"ordemServicoId\":\"11111111-1111-1111-1111-111111111111\",\"pagamentoId\":\"33333333-3333-3333-3333-333333333333\",\"status\":\"capturado\"}", "status de pagamento inválido: capturado.")]
    [InlineData("{\"eventId\":\"22222222-2222-2222-2222-222222222222\",\"tipo\":\"pagamento.resultado.v1\",\"ordemServicoId\":\"11111111-1111-1111-1111-111111111111\",\"pagamentoId\":\"33333333-3333-3333-3333-333333333333\",\"status\":\"Aprovado\"}", "status de pagamento inválido: Aprovado.")]
    [InlineData("{\"eventId\":\"22222222-2222-2222-2222-222222222222\",\"tipo\":\"pagamento.resultado.v1\",\"ordemServicoId\":\"11111111-1111-1111-1111-111111111111\",\"pagamentoId\":\"33333333-3333-3333-3333-333333333333\",\"status\":\"2\"}", "status de pagamento inválido: 2.")]
    [InlineData("{\"eventId\":\"22222222-2222-2222-2222-222222222222\",\"tipo\":\"pagamento.resultado.v1\",\"ordemServicoId\":\"11111111-1111-1111-1111-111111111111\",\"pagamentoId\":\"33333333-3333-3333-3333-333333333333\"}", "status de pagamento inválido: .")]
    [InlineData("{\"eventId\":\"22222222-2222-2222-2222-222222222222\",\"tipo\":\"ordem-servico.cancelar.v1\",\"ordemServicoId\":\"11111111-1111-1111-1111-111111111111\",\"motivo\":\"\\ud800\"}", "motivo ausente ou inválido: informe de 1 a 2000 caracteres, sem o caractere NUL.")]
    [InlineData("{\"eventId\":\"22222222-2222-2222-2222-222222222222\",\"tipo\":\"execucao.cancelada.v1\",\"ordemServicoId\":\"11111111-1111-1111-1111-111111111111\",\"execucaoId\":\"nao-e-guid\"}", "execucaoId inválido.")]
    [InlineData("{\"eventId\":\"22222222-2222-2222-2222-222222222222\",\"tipo\":\"orcamento.cancelado.v1\",\"ordemServicoId\":\"11111111-1111-1111-1111-111111111111\",\"orcamentoId\":\"nao-e-guid\"}", "orcamentoId inválido.")]
    public async Task RetryExhaustionDeadLetter_MalformedOrUnknownMessageGoesStraightToTheDeadLetterQueue(string corpo, string motivoEsperado)
    {
        var resultado = await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None);

        Assert.Equal(new ResultadoProcessamento(DesfechoMensagem.EnviadaParaDlq, null, 0), resultado);
        Assert.Empty(Esperas);
        var dlq = Assert.Single(Atual.Envio.Dlq);
        Assert.Equal(corpo, dlq.Corpo);
        Assert.StartsWith($"Mensagem inválida: {motivoEsperado}", dlq.Motivo, StringComparison.Ordinal);
        var alerta = Assert.Single(Atual.Alertas.Alertas);
        Assert.Equal("saga.mensagem-invalida", alerta.Codigo);
        Assert.StartsWith($"Mensagem da Saga descartada para a DLQ: {motivoEsperado}", alerta.Mensagem, StringComparison.Ordinal);
        Assert.Equal(0, (await Dynamo.ScanAsync(new ScanRequest { TableName = TabelasDynamo.TabelaSaga })).Count);
        Assert.Equal(0, (await Dynamo.ScanAsync(new ScanRequest { TableName = TabelasDynamo.TabelaEventos })).Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\0b")]
    [InlineData("TAMANHO_2001")]
    public async Task RetryExhaustionDeadLetter_CancellationMessageWithoutAValidReasonGoesStraightToTheDeadLetterQueue(string? motivo)
    {
        if (motivo == "TAMANHO_2001") motivo = new string('x', 2001);
        var (id, _, _) = await CriarOSAsync();
        var (corpo, _) = motivo is null
            ? Mensagem(TiposMensagem.OrdemServicoCancelar, id)
            : Mensagem(TiposMensagem.OrdemServicoCancelar, id, null, null, ("motivo", motivo));

        var resultado = await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None);

        Assert.Equal(new ResultadoProcessamento(DesfechoMensagem.EnviadaParaDlq, null, 0), resultado);
        var dlq = Assert.Single(Atual.Envio.Dlq);
        Assert.Equal(corpo, dlq.Corpo);
        Assert.Equal("Mensagem inválida: motivo ausente ou inválido: informe de 1 a 2000 caracteres, sem o caractere NUL.", dlq.Motivo);
        Assert.Equal(["saga.mensagem-invalida"], Atual.Alertas.Codigos);
        Assert.Null(await Atual.Sagas.ObterAsync(id, CancellationToken.None));
    }

    [Theory]
    [InlineData("execucao.falhou.v1", "a\0b")]
    [InlineData("execucao.falhou.v1", "TAMANHO_2001")]
    [InlineData("execucao.liberacao-recusada.v1", "TAMANHO_2001")]
    [InlineData("orcamento.cancelamento-recusado.v1", "TAMANHO_2001")]
    [InlineData("execucao.cancelamento-recusado.v1", "a\0b")]
    public async Task RetryExhaustionDeadLetter_OptionalReasonAbove2000CharactersOrWithNulGoesStraightToTheDeadLetterQueue(string tipo, string motivo)
    {
        if (motivo == "TAMANHO_2001") motivo = new string('x', 2001);
        var (corpo, _) = Mensagem(tipo, Guid.NewGuid(), null, null, ("motivo", motivo));

        var resultado = await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None);

        Assert.Equal(new ResultadoProcessamento(DesfechoMensagem.EnviadaParaDlq, null, 0), resultado);
        Assert.Equal("Mensagem inválida: motivo inválido: no máximo 2000 caracteres, sem o caractere NUL.", Assert.Single(Atual.Envio.Dlq).Motivo);
        Assert.Equal(["saga.mensagem-invalida"], Atual.Alertas.Codigos);
    }

    [Fact]
    public async Task RetryExhaustionDeadLetter_ReasonOfExactly2000CharactersIsAccepted()
    {
        var s = await IniciarSagaAsync();
        var motivo = new string('x', 2000);

        var resultado = await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", motivo));

        Assert.Equal(new ResultadoProcessamento(DesfechoMensagem.Processada, ResultadoMensagem.Aplicada, 1), resultado);
        Assert.Equal(motivo, Json.Ler(Atual.Envio.Enviados[1].Corpo).GetProperty("motivo").GetString());
        Assert.Empty(Atual.Envio.Dlq);
    }

    [Fact]
    public async Task RetryExhaustionDeadLetter_DeadLetterQueueFailureForAMessagePropagatesSoItIsNotDeleted()
    {
        Atual.Envio.FalhaDlq = () => new IOException("DLQ indisponível");

        await Assert.ThrowsAsync<IOException>(() => Atual.Processador.ProcessarAsync("lixo", CancellationToken.None));

        Assert.Empty(Atual.Envio.Dlq);
        Assert.Empty(Atual.Alertas.Alertas);
    }

    [Fact]
    public async Task RetryExhaustionDeadLetter_CommandThatCannotBePublishedGoesToTheDeadLetterQueueAfterFiveAttempts()
    {
        Atual.Envio.Falha = (_, _) => new IOException("SQS fora do ar");
        var s = await IniciarSagaAsync();
        Assert.Single(Atual.Envio.Tentativas);
        var atrasos = new List<TimeSpan>();

        // Antes do prazo o despachador não tenta de novo.
        Assert.Equal(0, await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None));
        Assert.Single(Atual.Envio.Tentativas);

        for (var i = 0; i < 4; i++)
        {
            var comando = Assert.Single((await SagaAsync(s.OrdemServicoId)).Comandos);
            Assert.Equal(i + 1, comando.Tentativas);
            var atraso = comando.ProximaTentativaEm!.Value - Relogio.Agora;
            atrasos.Add(atraso);
            Relogio.Avancar(atraso);
            Assert.Equal(1, await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None));
        }

        Assert.Equal(AtrasosEsperados, atrasos);
        Assert.Equal(5, Atual.Envio.Tentativas.Count);
        Assert.Single(Atual.Envio.Tentativas.Select(t => t.Corpo).Distinct());
        Assert.Empty(Atual.Envio.Enviados);
        var dlq = Assert.Single(Atual.Envio.Dlq);
        Assert.Equal(Atual.Envio.Tentativas[0].Corpo, dlq.Corpo);
        Assert.Equal("Comando ordem-servico.liberada.v1 não publicado após 5 tentativas: SQS fora do ar", dlq.Motivo);
        var saga = await SagaAsync(s.OrdemServicoId);
        var persistido = Assert.Single(saga.Comandos);
        Assert.False(persistido.Pendente);
        Assert.Equal(5, persistido.Tentativas);
        Assert.Equal(T0.AddSeconds(18.75), persistido.EncaminhadoParaDlqEm);
        Assert.Null(persistido.EnviadoEm);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, saga.Estado);
        var alerta = Assert.Single(Atual.Alertas.Alertas);
        Assert.Equal("saga.comando-esgotado", alerta.Codigo);
        Assert.Equal(s.OrdemServicoId, alerta.OrdemServicoId);
        Assert.Equal(persistido.EventId, alerta.EventId);
        Assert.Empty(Esperas);
        // Sem trabalho pendente: o despachador não volta a tentar o comando esgotado.
        Assert.Equal(0, await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None));
        Assert.Equal(5, Atual.Envio.Tentativas.Count);
    }

    [Fact]
    public async Task RetryExhaustionDeadLetter_DeadLetterQueueOutageKeepsTheCommandPendingAndAlerts()
    {
        Atual.Envio.Falha = (_, _) => new IOException("SQS fora do ar");
        Atual.Envio.FalhaDlq = () => new IOException("DLQ indisponível");
        var s = await IniciarSagaAsync();
        foreach (var atraso in AtrasosEsperados)
        {
            Relogio.Avancar(atraso);
            await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None);
        }

        var preso = Assert.Single((await SagaAsync(s.OrdemServicoId)).Comandos);
        Assert.True(preso.Pendente);
        Assert.Equal(6, preso.Tentativas);
        Assert.Equal(5, Atual.Envio.Tentativas.Count);
        Assert.Empty(Atual.Envio.Dlq);
        var alerta = Assert.Single(Atual.Alertas.Alertas);
        Assert.Equal("saga.dlq-indisponivel", alerta.Codigo);
        Assert.Contains("Falha também ao enviar à DLQ: DLQ indisponível", alerta.Mensagem);
        Assert.Equal(Relogio.Agora.AddSeconds(20), preso.ProximaTentativaEm); // 16s (base 1s, 5ª falha) + 25% de jitter, com o teto de 300s

        // A DLQ volta: o comando vai direto para ela, sem novas tentativas na fila principal.
        Atual.Envio.FalhaDlq = () => null;
        Relogio.Avancar(TimeSpan.FromSeconds(20));
        await Atual.Despachador.ExecutarCicloAsync(CancellationToken.None);

        Assert.Single(Atual.Envio.Dlq);
        Assert.Equal(5, Atual.Envio.Tentativas.Count);
        var final = Assert.Single((await SagaAsync(s.OrdemServicoId)).Comandos);
        Assert.False(final.Pendente);
        Assert.Equal(["saga.dlq-indisponivel", "saga.comando-esgotado"], Atual.Alertas.Codigos);
    }
}
