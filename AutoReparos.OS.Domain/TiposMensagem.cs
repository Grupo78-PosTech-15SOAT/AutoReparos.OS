namespace AutoReparos.OS.Domain;

/// <summary>Valores de <c>tipo</c> do contrato de mensagens da Saga (v1).</summary>
public static class TiposMensagem
{
    public const string OrcamentoAprovado = "orcamento.aprovado.v1";
    public const string ExecucaoConcluida = "execucao.concluida.v1";
    public const string ExecucaoFalhou = "execucao.falhou.v1";
    public const string ExecucaoLiberacaoRecusada = "execucao.liberacao-recusada.v1";
    public const string PagamentoResultado = "pagamento.resultado.v1";
    public const string OrdemServicoCancelar = "ordem-servico.cancelar.v1";
    public const string OrcamentoCancelado = "orcamento.cancelado.v1";
    public const string OrcamentoCancelamentoRecusado = "orcamento.cancelamento-recusado.v1";
    public const string ExecucaoCancelada = "execucao.cancelada.v1";
    public const string ExecucaoCancelamentoRecusado = "execucao.cancelamento-recusado.v1";
    public const string InsumosReservados = "insumos.reservados.v1";
    public const string InsumosReservaFalhou = "insumos.reserva-falhou.v1";
    public const string InsumosReservaCancelada = "insumos.reserva-cancelada.v1";

    public const string OrdemServicoLiberada = "ordem-servico.liberada.v1";
    public const string ExecucaoCancelar = "execucao.cancelar.v1";
    public const string ExecucaoReservarInsumos = "execucao.reservar-insumos.v1";
    public const string ExecucaoCancelarReserva = "execucao.cancelar-reserva.v1";
    public const string SagaLembrete = "saga.lembrete.v1";
    public const string CobrancaCancelarOrcamento = "cobranca.cancelar-orcamento.v1";
    public const string BillingOrcamentoCancelarLegado = "billing.orcamento.cancelar.v1";
    public const string BillingOrcamentoCancelar = CobrancaCancelarOrcamento;

    public static string NormalizarComando(string tipo) =>
        tipo == BillingOrcamentoCancelarLegado ? CobrancaCancelarOrcamento : tipo;

    public static readonly IReadOnlyList<string> Entrada =
    [
        OrcamentoAprovado, ExecucaoConcluida, ExecucaoFalhou, ExecucaoLiberacaoRecusada, PagamentoResultado,
        OrdemServicoCancelar, OrcamentoCancelado, OrcamentoCancelamentoRecusado, ExecucaoCancelada, ExecucaoCancelamentoRecusado,
        InsumosReservados, InsumosReservaFalhou, InsumosReservaCancelada
    ];
}
