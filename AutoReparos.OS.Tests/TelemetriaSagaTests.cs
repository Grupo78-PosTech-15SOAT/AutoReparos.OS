using System.Diagnostics.Metrics;
using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;

/// <summary>
/// C10 a C13 de infra-ci-observabilidade (D-66): contadores e gauges do <c>Meter</c> <c>AutoReparos.OS.Saga</c>.
/// Roda na coleção "saga" (serial), então só os testes desta classe tocam os contadores enquanto o ouvinte está ativo.
/// </summary>
public sealed class TelemetriaSagaTests(SagaAmbiente ambiente) : SagaTestBase(ambiente), IDisposable
{
    private static readonly Guid Oleo = Guid.Parse("1a5f0001-0000-4000-8000-0000000000aa");
    private readonly MeterListener ouvinte = new();
    private readonly List<(string Instrumento, long Valor, string? Passo)> medicoes = [];

    private void Ouvir()
    {
        ouvinte.InstrumentPublished = (instrumento, listener) =>
        {
            if (instrumento.Meter.Name == TelemetriaSaga.NomeMeter) listener.EnableMeasurementEvents(instrumento);
        };
        ouvinte.SetMeasurementEventCallback<long>((instrumento, valor, tags, _) =>
        {
            string? passo = null;
            foreach (var tag in tags) if (tag.Key == "passo") passo = tag.Value as string;
            lock (medicoes) medicoes.Add((instrumento.Name, valor, passo));
        });
        ouvinte.Start();
    }

    public void Dispose() => ouvinte.Dispose();

    private long Soma(string instrumento)
    {
        lock (medicoes) return medicoes.Where(m => m.Instrumento == instrumento).Sum(m => m.Valor);
    }

    private long[] Observar(string instrumento)
    {
        lock (medicoes) medicoes.RemoveAll(m => m.Instrumento == instrumento);
        ouvinte.RecordObservableInstruments();
        lock (medicoes) return medicoes.Where(m => m.Instrumento == instrumento).Select(m => m.Valor).ToArray();
    }

    private Task<ResultadoProcessamento> PagamentoAsync(Guid os, string status, Guid? eventId = null) =>
        ReceberAsync(TiposMensagem.PagamentoResultado, os, null, eventId, ("pagamentoId", Guid.NewGuid()), ("status", status), ("referencia", "pix-1"));

    [Fact]
    public async Task SagaConcluidaComPagamentoCapturado_SomaUmEmIniciadasEUmEmConcluidas()
    {
        Ouvir();
        var s = await IniciarSagaAsync();
        Assert.Equal(1, Soma("sagas_iniciadas_total"));
        Assert.Equal(0, Soma("sagas_concluidas_total"));

        await ReceberAsync(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId);
        await PagamentoAsync(s.OrdemServicoId, "Pendente");
        Assert.Equal(0, Soma("sagas_concluidas_total"));

        var captura = Guid.NewGuid();
        await PagamentoAsync(s.OrdemServicoId, "Capturado", captura);
        await PagamentoAsync(s.OrdemServicoId, "Capturado", captura);

        Assert.Equal(EstadoSaga.PagamentoCapturado, (await SagaAsync(s.OrdemServicoId)).Estado);
        Assert.Equal(1, Soma("sagas_iniciadas_total"));
        Assert.Equal(1, Soma("sagas_concluidas_total"));
    }

    [Fact]
    public async Task OrcamentoAprovadoRepetido_NaoContaUmSegundoInicio()
    {
        Ouvir();
        var s = await IniciarSagaAsync();

        await ReceberAsync(TiposMensagem.OrcamentoAprovado, s.OrdemServicoId, null, null, ("correlacaoId", s.CorrelacaoId), ("orcamentoId", s.OrcamentoId), ("clienteId", s.ClienteId), ("itens", Array.Empty<object>()));

        Assert.Equal(1, Soma("sagas_iniciadas_total"));
    }

    [Fact]
    public async Task LiberacaoRecusadaComReserva_SomaUmaCompensacaoPorComandoDespachadoComOPassoDoTipo()
    {
        Ouvir();
        var (id, clienteId, _) = await CriarOSAsync();
        var correlacao = Guid.NewGuid();
        var orcamentoId = Guid.NewGuid();
        var itens = new[] { new { insumoId = Oleo, quantidade = 2 } };
        await ReceberAsync(TiposMensagem.OrcamentoAprovado, id, null, null, ("correlacaoId", correlacao), ("orcamentoId", orcamentoId), ("clienteId", clienteId), ("itens", itens));
        await ReceberAsync(TiposMensagem.InsumosReservados, id, null, null, ("correlacaoId", correlacao), ("itens", Array.Empty<object>()));
        Assert.Equal(0, Soma("sagas_compensadas_total"));

        await ReceberAsync(TiposMensagem.ExecucaoLiberacaoRecusada, id, null, null, ("correlacaoId", correlacao), ("motivo", "oficina lotada"));
        await ReceberAsync(TiposMensagem.InsumosReservaCancelada, id, null, null, ("correlacaoId", correlacao));
        await ReceberAsync(TiposMensagem.OrcamentoCancelado, id, null, null, ("correlacaoId", correlacao), ("orcamentoId", orcamentoId));

        string[] compensacoes = ["execucao.cancelar-reserva.v1", "cobranca.cancelar-orcamento.v1", "execucao.cancelar.v1"];
        var enviadas = Atual.Envio.TiposEnviados.Where(compensacoes.Contains).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["cobranca.cancelar-orcamento.v1", "execucao.cancelar-reserva.v1"], enviadas);
        string?[] passos;
        lock (medicoes) passos = medicoes.Where(m => m.Instrumento == "sagas_compensadas_total").Select(m => m.Passo).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(enviadas, passos);
        Assert.Equal(2, Soma("sagas_compensadas_total"));
    }

    [Fact]
    public async Task Coleta_PublicaSagasParadasNaoTerminaisEProfundidadeDaDlq()
    {
        Ouvir();
        var parada = await IniciarSagaAsync();
        var concluida = await IniciarSagaAsync();
        await ReceberAsync(TiposMensagem.ExecucaoConcluida, concluida.OrdemServicoId);
        await PagamentoAsync(concluida.OrdemServicoId, "Capturado");
        Relogio.Avancar(TimeSpan.FromMinutes(31));
        await IniciarSagaAsync();
        var coletor = new ColetorMetricasSaga(Atual.Sagas, new DlqFalsa(() => 7), Relogio, new OpcoesSaga());

        await coletor.ColetarAsync(CancellationToken.None);

        Assert.Equal(EstadoSaga.ExecucaoLiberada, (await SagaAsync(parada.OrdemServicoId)).Estado);
        Assert.Equal([1L], Observar("sagas_paradas"));
        Assert.Equal([7L], Observar("saga_dlq_mensagens"));
    }

    [Fact]
    public async Task FalhaAoLerADlq_RetiraAAmostraSemLancarExcecao()
    {
        Ouvir();
        var leituras = new Queue<Func<long>>([() => 3, () => throw new InvalidOperationException("SQS indisponível")]);
        var coletor = new ColetorMetricasSaga(Atual.Sagas, new DlqFalsa(() => leituras.Dequeue()()), Relogio, new OpcoesSaga());
        await coletor.ColetarAsync(CancellationToken.None);
        Assert.Equal([3L], Observar("saga_dlq_mensagens"));

        var falha = await Record.ExceptionAsync(() => coletor.ColetarAsync(CancellationToken.None));

        Assert.Null(falha);
        Assert.Empty(Observar("saga_dlq_mensagens"));
    }

    private sealed class DlqFalsa(Func<long> ler) : IProfundidadeDlqSaga
    {
        public Task<long> LerAsync(CancellationToken cancellationToken) => Task.FromResult(ler());
    }
}
