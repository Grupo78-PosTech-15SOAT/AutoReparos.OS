using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;

/// <summary>C8 de saga-compensacoes: dois eventos concorrentes para a mesma Saga gravam uma única transição (escrita condicional por versão).</summary>
public sealed class SagaConcorrenciaTests(SagaAmbiente ambiente) : SagaTestBase(ambiente)
{
    private static readonly Guid Oleo = Guid.Parse("1a5f0001-0000-4000-8000-000000000001");

    /// <summary>Faz as duas leituras da Saga acontecerem antes de qualquer gravação: a corrida deixa de depender de sorte.</summary>
    private sealed class BarreiraDeLeitura(ISagaRepositorio inner, Barrier barreira) : ISagaRepositorio
    {
        private int lidas;

        public async Task<SagaOS?> ObterAsync(Guid ordemServicoId, CancellationToken cancellationToken)
        {
            var saga = await inner.ObterAsync(ordemServicoId, cancellationToken);
            if (Interlocked.Increment(ref lidas) == 1 && !barreira.SignalAndWait(TimeSpan.FromSeconds(20), cancellationToken)) throw new TimeoutException("Barreira de leitura expirou.");
            return saga;
        }

        public Task<bool> EventoProcessadoAsync(Guid eventId, CancellationToken cancellationToken) => inner.EventoProcessadoAsync(eventId, cancellationToken);
        public Task<ResultadoGravacao> GravarAsync(SagaOS saga, EventoProcessado? evento, CancellationToken cancellationToken) => inner.GravarAsync(saga, evento, cancellationToken);
        public Task<IReadOnlyList<SagaOS>> ListarAsync(EstadoSaga? estado, DateTimeOffset? atualizadaAte, CancellationToken cancellationToken) => inner.ListarAsync(estado, atualizadaAte, cancellationToken);
        public Task<IReadOnlyList<Guid>> ListarComTrabalhoPendenteAsync(DateTimeOffset ate, CancellationToken cancellationToken) => inner.ListarComTrabalhoPendenteAsync(ate, cancellationToken);
    }

    private async Task<Guid> SagaReservandoAsync()
    {
        var (id, clienteId, _) = await CriarOSAsync();
        var (corpo, _) = Mensagem(TiposMensagem.OrcamentoAprovado, id, Guid.NewGuid(), null, ("orcamentoId", Guid.NewGuid()), ("clienteId", clienteId), ("itens", new[] { new { insumoId = Oleo, quantidade = 1 } }));
        await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None);
        Assert.Equal(EstadoSaga.ReservandoInsumos, (await SagaAsync(id)).Estado);
        return id;
    }

    [Fact]
    public async Task EventosConcorrentesGravamUmaTransicao()
    {
        var id = await SagaReservandoAsync();
        var barreira = new Barrier(2);
        var a = NovosComponentes(r => new BarreiraDeLeitura(r, barreira));
        var b = NovosComponentes(r => new BarreiraDeLeitura(r, barreira));

        // `reservados` e `reserva-falhou` chegam juntos para a mesma Saga em ReservandoInsumos.
        var (reservados, _) = Mensagem(TiposMensagem.InsumosReservados, id, null, null, ("itens", Array.Empty<object>()));
        var (falhou, _) = Mensagem(TiposMensagem.InsumosReservaFalhou, id, null, null, ("motivo", "Saldo insuficiente."), ("itensEmFalta", Array.Empty<object>()));
        var resultados = await Task.WhenAll(
            a.Processador.ProcessarAsync(reservados, CancellationToken.None),
            b.Processador.ProcessarAsync(falhou, CancellationToken.None));

        // Uma transição aplicada; a outra foi reavaliada contra o estado novo e rejeitada com registro.
        Assert.Equal([ResultadoMensagem.Aplicada, ResultadoMensagem.Rejeitada], resultados.Select(r => r.Resultado!.Value).Order());
        var saga = await SagaAsync(id);
        Assert.Contains(saga.Estado, new[] { EstadoSaga.ExecucaoLiberada, EstadoSaga.AguardandoInsumos });
        Assert.Single(saga.Registros, r => r.Evento is TiposMensagem.InsumosReservados or TiposMensagem.InsumosReservaFalhou && !r.Descricao.StartsWith("Transição rejeitada", StringComparison.Ordinal));
        Assert.Single(saga.Registros, r => r.Evento is TiposMensagem.InsumosReservados or TiposMensagem.InsumosReservaFalhou && r.Descricao.StartsWith("Transição rejeitada", StringComparison.Ordinal));
        // Os efeitos da transição vencedora não se duplicam: ou uma liberação, ou um alerta de espera.
        var liberacoes = saga.Comandos.Count(c => c.Tipo == TiposMensagem.OrdemServicoLiberada);
        Assert.Equal(saga.Estado == EstadoSaga.ExecucaoLiberada ? 1 : 0, liberacoes);
    }

    [Fact]
    public async Task EventosConcorrentesGravamUmaTransicao_OMesmoEventoNaoSeAplicaDuasVezes()
    {
        var id = await SagaReservandoAsync();
        var barreira = new Barrier(2);
        var a = NovosComponentes(r => new BarreiraDeLeitura(r, barreira));
        var b = NovosComponentes(r => new BarreiraDeLeitura(r, barreira));
        var (corpo, _) = Mensagem(TiposMensagem.InsumosReservados, id, null, null, ("itens", Array.Empty<object>()));

        var resultados = await Task.WhenAll(a.Processador.ProcessarAsync(corpo, CancellationToken.None), b.Processador.ProcessarAsync(corpo, CancellationToken.None));

        Assert.Equal([ResultadoMensagem.Aplicada, ResultadoMensagem.Duplicada], resultados.Select(r => r.Resultado!.Value).Order());
        var saga = await SagaAsync(id);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, saga.Estado);
        Assert.Single(saga.Comandos, c => c.Tipo == TiposMensagem.OrdemServicoLiberada);
    }
}
