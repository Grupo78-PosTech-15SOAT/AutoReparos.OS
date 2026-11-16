using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;

/// <summary>C10 de saga-compensacoes: prazos de negócio publicam <c>saga.lembrete.v1</c> e nunca expiram nem cancelam o orçamento.</summary>
public sealed class SagaLembretesTests(SagaAmbiente ambiente) : SagaTestBase(ambiente)
{
    private static readonly string[] Compensacoes = ["cobranca.cancelar-orcamento.v1", "execucao.cancelar.v1", "execucao.cancelar-reserva.v1"];

    private Task<int> CicloAsync() => Atual.Despachador.ExecutarCicloAsync(CancellationToken.None);

    private IEnumerable<(DestinoComando Destino, string Corpo)> Lembretes => Atual.Envio.Enviados.Where(e => Json.Tipo(e.Corpo) == "saga.lembrete.v1");

    [Fact]
    public async Task LembreteNaoExpiraOrcamento_AguardandoPagamentoLembraACadaDiaESemCancelar()
    {
        var s = await IniciarSagaAsync();
        await ReceberAsync(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId, null, null, ("execucaoId", Guid.NewGuid()));
        Assert.Empty(Lembretes);

        Relogio.Avancar(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await CicloAsync());
        Assert.Empty(Lembretes);

        Relogio.Avancar(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await CicloAsync());

        var (destino, corpo) = Assert.Single(Lembretes);
        Assert.Equal(DestinoComando.Cadastros, destino);
        var lembrete = Json.Ler(corpo);
        Assert.Equal(s.OrdemServicoId, lembrete.GetProperty("ordemServicoId").GetGuid());
        Assert.Equal(s.CorrelacaoId, lembrete.GetProperty("correlacaoId").GetGuid());
        Assert.Equal("aguardando-pagamento", lembrete.GetProperty("motivo").GetString());

        // Semanas depois a Saga segue aguardando: um lembrete por dia e nenhuma compensação, expiração ou alerta de falha.
        for (var dia = 0; dia < 30; dia++)
        {
            Relogio.Avancar(TimeSpan.FromHours(24));
            await CicloAsync();
        }
        Assert.Equal(31, Lembretes.Count());
        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.ExecucaoConcluida, saga.Estado);
        Assert.Empty(Atual.Envio.TiposEnviados.Intersect(Compensacoes));
        Assert.All(saga.Etapas, e => Assert.NotEqual(SituacaoEtapa.Compensando, e.Situacao));
        Assert.Empty(Atual.Alertas.Alertas);
    }

    [Fact]
    public async Task LembreteNaoExpiraOrcamento_PagamentoCapturadoEncerraOsLembretes()
    {
        var s = await IniciarSagaAsync();
        await ReceberAsync(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId, null, null, ("execucaoId", Guid.NewGuid()));
        await ReceberAsync(TiposMensagem.PagamentoResultado, s.OrdemServicoId, null, null, ("pagamentoId", Guid.NewGuid()), ("status", "Capturado"), ("referencia", "mp-1"));
        Assert.Equal(EstadoSaga.PagamentoCapturado, (await SagaAsync(s.OrdemServicoId)).Estado);

        Relogio.Avancar(TimeSpan.FromDays(5));

        Assert.Equal(0, await CicloAsync());
        Assert.Empty(Lembretes);
    }

    [Fact]
    public async Task LembreteNaoExpiraOrcamento_AguardandoInsumosLembraJuntoComOAlertaDiario()
    {
        var (id, clienteId, _) = await CriarOSAsync();
        var correlacao = Guid.NewGuid();
        var itens = new[] { new { insumoId = Guid.NewGuid(), quantidade = 1 } };
        var (corpo, _) = Mensagem(TiposMensagem.OrcamentoAprovado, id, correlacao, null, ("orcamentoId", Guid.NewGuid()), ("clienteId", clienteId), ("itens", itens));
        await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None);
        await ReceberAsync(TiposMensagem.InsumosReservaFalhou, id, null, null, ("correlacaoId", correlacao), ("motivo", "Saldo insuficiente."), ("itensEmFalta", Array.Empty<object>()));
        Assert.Empty(Lembretes);

        Relogio.Avancar(TimeSpan.FromHours(24));
        await CicloAsync();

        var (destino, enviado) = Assert.Single(Lembretes);
        Assert.Equal(DestinoComando.Cadastros, destino);
        Assert.Equal("aguardando-insumos", Json.Ler(enviado).GetProperty("motivo").GetString());
        Assert.Equal(EstadoSaga.AguardandoInsumos, (await SagaAsync(id)).Estado);
        Assert.Empty(Atual.Envio.TiposEnviados.Intersect(Compensacoes));
    }

    [Fact]
    public async Task LembreteNaoExpiraOrcamento_CadastrosForaDoArNaoAtrasaACompensacao()
    {
        var (id, clienteId, _) = await CriarOSAsync();
        var correlacao = Guid.NewGuid();
        var itens = new[] { new { insumoId = Guid.NewGuid(), quantidade = 1 } };
        var (corpo, _) = Mensagem(TiposMensagem.OrcamentoAprovado, id, correlacao, null, ("orcamentoId", Guid.NewGuid()), ("clienteId", clienteId), ("itens", itens));
        await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None);
        await ReceberAsync(TiposMensagem.InsumosReservaFalhou, id, null, null, ("correlacaoId", correlacao), ("motivo", "Saldo insuficiente."), ("itensEmFalta", Array.Empty<object>()));
        Atual.Envio.Falha = (destino, _) => destino == DestinoComando.Cadastros ? new InvalidOperationException("cadastros fora do ar") : null;
        Relogio.Avancar(TimeSpan.FromHours(24));
        await CicloAsync();
        var preso = Assert.Single((await SagaAsync(id)).Comandos, c => c.Pendente);
        Assert.Equal("saga.lembrete.v1", preso.Tipo);

        await ReceberAsync(TiposMensagem.OrdemServicoCancelar, id, null, null, ("motivo", "cliente desistiu"));

        // O lembrete segue em backoff, mas a compensação do orçamento sai na hora.
        Assert.Contains("cobranca.cancelar-orcamento.v1", Atual.Envio.TiposEnviados);
    }
}
