using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;

/// <summary>C1 a C4 e C14 de saga-compensacoes: reserva de insumos antes de liberar, espera de saldo e compensação em ordem inversa.</summary>
public sealed class SagaInsumosTests(SagaAmbiente ambiente) : SagaTestBase(ambiente)
{
    private static readonly Guid Oleo = Guid.Parse("1a5f0001-0000-4000-8000-000000000001");
    private static readonly Guid Filtro = Guid.Parse("1a5f0001-0000-4000-8000-000000000002");

    /// <summary>Entrega <c>orcamento.aprovado.v1</c> com insumos: a Saga fica em ReservandoInsumos e só o comando de reserva sai.</summary>
    private async Task<SagaIniciada> AprovarComInsumosAsync()
    {
        var (id, clienteId, veiculoId) = await CriarOSAsync();
        var orcamentoId = Guid.NewGuid();
        var correlacao = Guid.NewGuid();
        var itens = new[] { new { insumoId = Oleo, quantidade = 4 }, new { insumoId = Filtro, quantidade = 1 } };
        var (corpo, _) = Mensagem(TiposMensagem.OrcamentoAprovado, id, correlacao, null, ("orcamentoId", orcamentoId), ("clienteId", clienteId), ("itens", itens));
        var resultado = await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None);
        Assert.Equal(DesfechoMensagem.Processada, resultado.Desfecho);
        return new SagaIniciada(id, orcamentoId, clienteId, veiculoId, correlacao);
    }

    private Task<ResultadoProcessamento> ReservadosAsync(SagaIniciada s) =>
        ReceberAsync(TiposMensagem.InsumosReservados, s.OrdemServicoId, null, null, ("correlacaoId", s.CorrelacaoId), ("itens", Array.Empty<object>()));

    private Task<ResultadoProcessamento> SemSaldoAsync(SagaIniciada s) =>
        ReceberAsync(TiposMensagem.InsumosReservaFalhou, s.OrdemServicoId, null, null, ("correlacaoId", s.CorrelacaoId), ("motivo", "Saldo insuficiente."), ("itensEmFalta", Array.Empty<object>()));

    [Fact]
    public async Task AprovacaoReservaAntesDeLiberar()
    {
        var s = await AprovarComInsumosAsync();

        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.ReservandoInsumos, saga.Estado);
        Assert.Equal(["execucao.reservar-insumos.v1"], Atual.Envio.TiposEnviados);
        Assert.DoesNotContain("ordem-servico.liberada.v1", Atual.Envio.TiposEnviados);
        Assert.Equal(DestinoComando.Execucao, Atual.Envio.Enviados[0].Destino);
        var comando = Json.Ler(Atual.Envio.Enviados[0].Corpo);
        Assert.Equal(s.OrdemServicoId, comando.GetProperty("ordemServicoId").GetGuid());
        Assert.Equal(s.CorrelacaoId, comando.GetProperty("correlacaoId").GetGuid());
        Assert.Equal([(Oleo, 4), (Filtro, 1)], comando.GetProperty("itens").EnumerateArray().Select(i => (i.GetProperty("insumoId").GetGuid(), i.GetProperty("quantidade").GetInt32())));
        // Comando e transição foram persistidos juntos: o item da Saga já traz a reserva enviada.
        Assert.Single(saga.Comandos);
        Assert.Equal([new ItemReservaSaga(Oleo, 4), new ItemReservaSaga(Filtro, 1)], saga.Itens);
        // A OS só é liberada na confirmação: ainda não há EmExecucao.
        await AssertOSAsync(s.OrdemServicoId, "OrcamentoAprovado", "OrcamentoAprovado", "Recebida");
    }

    [Fact]
    public async Task AprovacaoSemItensVaiParaDlq()
    {
        var (id, clienteId, _) = await CriarOSAsync();
        var (corpo, _) = Mensagem(TiposMensagem.OrcamentoAprovado, id, Guid.NewGuid(), null, ("orcamentoId", Guid.NewGuid()), ("clienteId", clienteId));

        var resultado = await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None);

        Assert.Equal(DesfechoMensagem.EnviadaParaDlq, resultado.Desfecho);
        Assert.Single(Atual.Envio.Dlq);
        Assert.Contains("itens", Atual.Envio.Dlq[0].Motivo);
        Assert.Null(await Atual.Sagas.ObterAsync(id, CancellationToken.None));
        Assert.Empty(Atual.Envio.Enviados);
    }

    [Fact]
    public async Task ReservadosLiberaExecucao()
    {
        var s = await AprovarComInsumosAsync();

        var resultado = await ReservadosAsync(s);

        Assert.Equal(ResultadoMensagem.Aplicada, resultado.Resultado);
        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, saga.Estado);
        Assert.Equal(SituacaoEtapa.Concluida, saga.Etapa(5).Situacao);
        Assert.Equal(["execucao.reservar-insumos.v1", "ordem-servico.liberada.v1"], Atual.Envio.TiposEnviados);
        await AssertOSAsync(s.OrdemServicoId, "EmExecucao", "EmExecucao", "OrcamentoAprovado", "Recebida");
    }

    [Fact]
    public async Task SemSaldoAguardaInsumos()
    {
        var s = await AprovarComInsumosAsync();

        await SemSaldoAsync(s);

        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.AguardandoInsumos, saga.Estado);
        // Sem compensar: nenhum comando de cancelamento, e a etapa da reserva segue sem o que desfazer.
        Assert.Equal(["execucao.reservar-insumos.v1"], Atual.Envio.TiposEnviados);
        Assert.Equal(SituacaoEtapa.Pendente, saga.Etapa(5).Situacao);
        Assert.Equal(["saga.aguardando-insumos"], Atual.Alertas.Codigos);

        // Um alerta por dia: antes de 24 h nada novo, depois um só.
        Relogio.Avancar(TimeSpan.FromHours(23));
        await Atual.Orquestrador.ProgredirAsync(s.OrdemServicoId, CancellationToken.None);
        Assert.Single(Atual.Alertas.Codigos);
        Relogio.Avancar(TimeSpan.FromHours(2));
        await Atual.Orquestrador.ProgredirAsync(s.OrdemServicoId, CancellationToken.None);
        await Atual.Orquestrador.ProgredirAsync(s.OrdemServicoId, CancellationToken.None);
        Assert.Equal(["saga.aguardando-insumos", "saga.aguardando-insumos"], Atual.Alertas.Codigos);

        // Retentar reenvia a reserva com um comando novo.
        var retentativa = await Atual.Orquestrador.RetentarReservaAsync(s.OrdemServicoId, "admin", "saldo reposto", CancellationToken.None);
        Assert.Equal(ResultadoRetentativa.Aceita, retentativa);
        Assert.Equal(EstadoSaga.ReservandoInsumos, (await SagaAsync(s.OrdemServicoId)).Estado);
        Assert.Equal(["execucao.reservar-insumos.v1", "execucao.reservar-insumos.v1"], Atual.Envio.TiposEnviados);
        Assert.NotEqual(Json.Ler(Atual.Envio.Enviados[0].Corpo).GetProperty("eventId").GetGuid(), Json.Ler(Atual.Envio.Enviados[1].Corpo).GetProperty("eventId").GetGuid());
    }

    [Fact]
    public async Task SemSaldoAguardaInsumos_CancelamentoCompensaSoOOrcamento()
    {
        var s = await AprovarComInsumosAsync();
        await SemSaldoAsync(s);

        await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "sem previsão de estoque"));

        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.Compensando, saga.Estado);
        Assert.Equal(["execucao.reservar-insumos.v1", "cobranca.cancelar-orcamento.v1"], Atual.Envio.TiposEnviados);
        Assert.False(saga.AguardaAlertaInsumos);
    }

    [Fact]
    public async Task CompensaReservaEOrcamentoEmOrdemInversa()
    {
        var s = await AprovarComInsumosAsync();
        await ReservadosAsync(s);

        await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "cliente desistiu"));
        await ReceberAsync(TiposMensagem.ExecucaoCancelada, s.OrdemServicoId, null, null, ("execucaoId", Guid.NewGuid()));
        Assert.Equal(["execucao.reservar-insumos.v1", "ordem-servico.liberada.v1", "execucao.cancelar.v1", "execucao.cancelar-reserva.v1"], Atual.Envio.TiposEnviados);
        Assert.Equal(DestinoComando.Execucao, Atual.Envio.Enviados[3].Destino);

        await ReceberAsync(TiposMensagem.InsumosReservaCancelada, s.OrdemServicoId, null, null, ("correlacaoId", s.CorrelacaoId));
        Assert.Equal("cobranca.cancelar-orcamento.v1", Atual.Envio.TiposEnviados.Last());
        Assert.Equal(DestinoComando.Billing, Atual.Envio.Enviados[4].Destino);

        await ReceberAsync(TiposMensagem.OrcamentoCancelado, s.OrdemServicoId, null, null, ("orcamentoId", s.OrcamentoId));

        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.Compensada, saga.Estado);
        Assert.Equal(["confirmada", "confirmada", "confirmada"], new[] { 2, 5, 1 }.Select(n => saga.Etapa(n).ResultadoCompensacao));
        Assert.Empty(Atual.Alertas.Alertas);
    }

    [Fact]
    public async Task CancelarReservaSoDepoisDaConfirmacao()
    {
        var s = await AprovarComInsumosAsync();

        var cedo = await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "mudei de ideia"));

        Assert.Equal(ResultadoMensagem.Rejeitada, cedo.Resultado);
        var antes = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.ReservandoInsumos, antes.Estado);
        Assert.DoesNotContain(antes.Comandos, c => c.Tipo == "execucao.cancelar-reserva.v1");
        Assert.Equal(["execucao.reservar-insumos.v1"], Atual.Envio.TiposEnviados);

        await ReservadosAsync(s);
        await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "mudei de ideia"));
        await ReceberAsync(TiposMensagem.ExecucaoCancelada, s.OrdemServicoId, null, null, ("execucaoId", Guid.NewGuid()));

        Assert.Contains("execucao.cancelar-reserva.v1", Atual.Envio.TiposEnviados);
        Assert.True(Atual.Envio.TiposEnviados.ToList().IndexOf("execucao.cancelar-reserva.v1") > Atual.Envio.TiposEnviados.ToList().IndexOf("ordem-servico.liberada.v1"));
    }
}
