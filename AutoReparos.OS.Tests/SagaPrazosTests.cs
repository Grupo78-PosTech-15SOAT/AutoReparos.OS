using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;

/// <summary>C5 e C9 de saga-compensacoes: prazos técnicos da reserva, da liberação e da compensação, com reenvios.</summary>
public sealed class SagaPrazosTests(SagaAmbiente ambiente) : SagaTestBase(ambiente)
{
    private static readonly Guid Oleo = Guid.Parse("1a5f0002-0000-4000-8000-000000000001");

    private async Task<SagaIniciada> AprovarComInsumosAsync()
    {
        var (id, clienteId, veiculoId) = await CriarOSAsync();
        var orcamentoId = Guid.NewGuid();
        var correlacao = Guid.NewGuid();
        var (corpo, _) = Mensagem(TiposMensagem.OrcamentoAprovado, id, correlacao, null, ("orcamentoId", orcamentoId), ("clienteId", clienteId), ("itens", new[] { new { insumoId = Oleo, quantidade = 2 } }));
        Assert.Equal(DesfechoMensagem.Processada, (await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None)).Desfecho);
        return new SagaIniciada(id, orcamentoId, clienteId, veiculoId, correlacao);
    }

    private Task<int> CicloAsync() => Atual.Despachador.ExecutarCicloAsync(CancellationToken.None);

    [Fact]
    public async Task PrazoSemRespostaCompensa_ReservaSemRespostaCompensaReservaEOrcamento()
    {
        var s = await AprovarComInsumosAsync();

        Relogio.Avancar(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await CicloAsync());
        Assert.Equal(EstadoSaga.ReservandoInsumos, (await SagaAsync(s.OrdemServicoId)).Estado);

        Relogio.Avancar(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await CicloAsync());

        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.Compensando, saga.Estado);
        Assert.Equal(SagaOS.MotivoSemResposta, saga.MotivoCompensacao);
        // A reserva sem resposta é tratada como possivelmente feita: o cancelamento da reserva sai mesmo sem confirmação.
        Assert.Equal(["execucao.reservar-insumos.v1", "execucao.cancelar-reserva.v1"], Atual.Envio.TiposEnviados);
        Assert.Equal(["saga.passo-sem-resposta"], Atual.Alertas.Codigos);

        await ReceberAsync(TiposMensagem.InsumosReservaCancelada, s.OrdemServicoId, null, null, ("correlacaoId", s.CorrelacaoId));
        await ReceberAsync(TiposMensagem.OrcamentoCancelado, s.OrdemServicoId, null, null, ("orcamentoId", s.OrcamentoId));

        Assert.Equal(EstadoSaga.Compensada, (await SagaAsync(s.OrdemServicoId)).Estado);
        Assert.Equal("cobranca.cancelar-orcamento.v1", Atual.Envio.TiposEnviados.Last());
    }

    [Fact]
    public async Task PrazoSemRespostaCompensa_CompensacaoSemConfirmacaoTerminaEmCompensacaoFalhouComAlerta()
    {
        var s = await AprovarComInsumosAsync();
        Relogio.Avancar(TimeSpan.FromMinutes(2));
        await CicloAsync();
        Assert.Equal(EstadoSaga.Compensando, (await SagaAsync(s.OrdemServicoId)).Estado);

        Relogio.Avancar(TimeSpan.FromMinutes(15));
        await CicloAsync();

        Assert.Equal(EstadoSaga.CompensacaoFalhou, (await SagaAsync(s.OrdemServicoId)).Estado);
        Assert.Equal(["saga.passo-sem-resposta", "saga.compensacao-sem-resposta"], Atual.Alertas.Codigos);
    }

    [Fact]
    public async Task PrazoSemRespostaCompensa_LiberacaoNaoEntregueNaoCompensaAExecucao()
    {
        var s = await AprovarComInsumosAsync();
        Atual.Envio.Falha = (_, corpo) => Json.Tipo(corpo) == "ordem-servico.liberada.v1" ? new InvalidOperationException("fila fora do ar") : null;
        await ReceberAsync(TiposMensagem.InsumosReservados, s.OrdemServicoId, null, null, ("correlacaoId", s.CorrelacaoId), ("itens", Array.Empty<object>()));
        Assert.Equal(EstadoSaga.ExecucaoLiberada, (await SagaAsync(s.OrdemServicoId)).Estado);

        Relogio.Avancar(TimeSpan.FromMinutes(2));
        await CicloAsync();

        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.Compensando, saga.Estado);
        Assert.Equal(SagaOS.MotivoSemResposta, saga.MotivoCompensacao);
        Assert.Equal(SituacaoEtapa.Pendente, saga.Etapa(2).Situacao);
        // Só reserva e orçamento têm o que desfazer: a execução nunca recebeu a liberação.
        Assert.Equal(["execucao.reservar-insumos.v1", "execucao.cancelar-reserva.v1"], Atual.Envio.TiposEnviados);
        Assert.DoesNotContain(saga.Comandos, c => c.Tipo == "execucao.cancelar.v1");
    }

    [Fact]
    public async Task PrazoTecnicoReenviaEEsgota_ReservaReenviaTresVezesEDepoisCompensa()
    {
        Atual = NovosComponentes(maxReenvios: 3);
        var s = await AprovarComInsumosAsync();

        for (var reenvio = 1; reenvio <= 3; reenvio++)
        {
            Relogio.Avancar(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
            Assert.Equal(0, await CicloAsync());
            Relogio.Avancar(TimeSpan.FromSeconds(1));
            Assert.Equal(1, await CicloAsync());
            Assert.Equal(EstadoSaga.ReservandoInsumos, (await SagaAsync(s.OrdemServicoId)).Estado);
            Assert.Equal(reenvio + 1, Atual.Envio.TiposEnviados.Count(t => t == "execucao.reservar-insumos.v1"));
        }
        // Cada reenvio leva um eventId novo, com o mesmo conteúdo.
        var ids = Atual.Envio.Enviados.Select(e => Json.Ler(e.Corpo).GetProperty("eventId").GetGuid()).Distinct().ToList();
        Assert.Equal(4, ids.Count);
        Assert.Equal(["saga.passo-reenviado", "saga.passo-reenviado", "saga.passo-reenviado"], Atual.Alertas.Codigos);

        Relogio.Avancar(TimeSpan.FromMinutes(2));
        await CicloAsync();

        Assert.Equal(EstadoSaga.Compensando, (await SagaAsync(s.OrdemServicoId)).Estado);
        Assert.Equal("execucao.cancelar-reserva.v1", Atual.Envio.TiposEnviados.Last());
        Assert.Equal("saga.passo-sem-resposta", Atual.Alertas.Codigos.Last());
    }

    [Fact]
    public async Task PrazoTecnicoReenviaEEsgota_RespostaDepoisDoReenvioEncerraOPrazo()
    {
        Atual = NovosComponentes(maxReenvios: 3);
        var s = await AprovarComInsumosAsync();
        Relogio.Avancar(TimeSpan.FromMinutes(2));
        await CicloAsync();

        await ReceberAsync(TiposMensagem.InsumosReservados, s.OrdemServicoId, null, null, ("correlacaoId", s.CorrelacaoId), ("itens", Array.Empty<object>()));

        var saga = await SagaAsync(s.OrdemServicoId);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, saga.Estado);
        Assert.Null(saga.PrazoPassoEm);
        Relogio.Avancar(TimeSpan.FromMinutes(30));
        Assert.Equal(0, await CicloAsync());
    }

    [Fact]
    public async Task PrazoTecnicoReenviaEEsgota_CompensacaoReenviaTresVezesAntesDeFalhar()
    {
        Atual = NovosComponentes(maxReenvios: 3);
        var s = await AprovarComInsumosAsync();
        await ReceberAsync(TiposMensagem.InsumosReservados, s.OrdemServicoId, null, null, ("correlacaoId", s.CorrelacaoId), ("itens", Array.Empty<object>()));
        await ReceberAsync(TiposMensagem.OrdemServicoCancelar, s.OrdemServicoId, null, null, ("motivo", "desistência"));
        Assert.Equal(1, Atual.Envio.TiposEnviados.Count(t => t == "execucao.cancelar.v1"));

        for (var reenvio = 1; reenvio <= 3; reenvio++)
        {
            Relogio.Avancar(TimeSpan.FromMinutes(15));
            await CicloAsync();
            Assert.Equal(EstadoSaga.Compensando, (await SagaAsync(s.OrdemServicoId)).Estado);
            Assert.Equal(reenvio + 1, Atual.Envio.TiposEnviados.Count(t => t == "execucao.cancelar.v1"));
        }

        Relogio.Avancar(TimeSpan.FromMinutes(15));
        await CicloAsync();

        Assert.Equal(EstadoSaga.CompensacaoFalhou, (await SagaAsync(s.OrdemServicoId)).Estado);
        Assert.Equal("saga.compensacao-sem-resposta", Atual.Alertas.Codigos.Last());
    }
}
