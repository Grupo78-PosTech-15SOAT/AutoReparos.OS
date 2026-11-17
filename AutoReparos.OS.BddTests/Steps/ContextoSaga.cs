using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;

/// <summary>
/// Cenário BDD sobre a Saga real do OS (orquestrador, processador, DynamoDB Local e PostgreSQL), com Cobrança e Execução
/// simuladas pelas mensagens que elas enviariam. Reaproveita o <see cref="SagaTestBase"/> e expõe só o necessário aos passos.
/// </summary>
public sealed class ContextoSaga(SagaAmbiente ambiente) : SagaTestBase(ambiente)
{
    private static readonly Guid Oleo = Guid.Parse("1a5f0001-0000-4000-8000-000000000001");
    private static readonly Guid Filtro = Guid.Parse("1a5f0001-0000-4000-8000-000000000002");

    public SagaIniciada? Saga { get; private set; }
    public Guid OrdemServicoId => Saga!.OrdemServicoId;
    public IReadOnlyList<string> Enviados => Atual.Envio.TiposEnviados.ToList();
    /// <summary>Comandos de compensação enviados, na ordem de envio (os tipos <c>*.cancelar*</c> que a Saga despacha).</summary>
    public IReadOnlyList<string> Compensacoes => Enviados.Where(t => t.Contains("cancelar", StringComparison.Ordinal)).ToList();
    public IReadOnlyList<string> Alertas => Atual.Alertas.Codigos;

    /// <summary>Aprova o orçamento da OS com insumos: a Saga passa a reservar insumos no Execução.</summary>
    public Task AprovarOrcamentoComInsumosAsync() =>
        AprovarOrcamentoAsync([new { insumoId = Oleo, quantidade = 4 }, new { insumoId = Filtro, quantidade = 1 }]);

    /// <summary>Aprova um orçamento só de mão de obra (lista de itens vazia): não há o que reservar.</summary>
    public Task AprovarOrcamentoSemInsumosAsync() => AprovarOrcamentoAsync([]);

    private async Task AprovarOrcamentoAsync(object[] itens)
    {
        var (id, clienteId, veiculoId) = await CriarOSAsync();
        var orcamentoId = Guid.NewGuid();
        var correlacao = Guid.NewGuid();
        var (corpo, _) = Mensagem(TiposMensagem.OrcamentoAprovado, id, correlacao, null, ("orcamentoId", orcamentoId), ("clienteId", clienteId), ("itens", itens));
        Assert.Equal(DesfechoMensagem.Processada, (await Atual.Processador.ProcessarAsync(corpo, CancellationToken.None)).Desfecho);
        Saga = new SagaIniciada(id, orcamentoId, clienteId, veiculoId, correlacao);
    }

    /// <summary>Entrega à Saga uma mensagem de Cobrança ou Execução e exige que ela tenha sido processada.</summary>
    public async Task ReceberAsync(string tipo, params (string Chave, object? Valor)[] extras) =>
        Assert.Equal(DesfechoMensagem.Processada, (await ReceberAsync(tipo, OrdemServicoId, null, null, extras)).Desfecho);

    public Task ReceberInsumosReservadosAsync() => ReceberAsync(TiposMensagem.InsumosReservados, ("correlacaoId", Saga!.CorrelacaoId), ("itens", Array.Empty<object>()));

    public Task ReceberReservaFalhouAsync() =>
        ReceberAsync(TiposMensagem.InsumosReservaFalhou, ("correlacaoId", Saga!.CorrelacaoId), ("motivo", "Saldo insuficiente."), ("itensEmFalta", Array.Empty<object>()));

    public Task ReceberPagamentoAsync(string status) =>
        ReceberAsync(TiposMensagem.PagamentoResultado, ("pagamentoId", Guid.NewGuid()), ("status", status), ("referencia", "pix-bdd"));

    public Task ReceberReservaCanceladaAsync() => ReceberAsync(TiposMensagem.InsumosReservaCancelada, ("correlacaoId", Saga!.CorrelacaoId));

    public Task ReceberOrcamentoCanceladoAsync() => ReceberAsync(TiposMensagem.OrcamentoCancelado, ("orcamentoId", Saga!.OrcamentoId));

    public async Task<EstadoSaga> EstadoAsync() => (await SagaAsync(OrdemServicoId)).Estado;

    public async Task<string> StatusOSAsync() => (await OSAsync(OrdemServicoId)).Status;
}
