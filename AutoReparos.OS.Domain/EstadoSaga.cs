namespace AutoReparos.OS.Domain;

public enum EstadoSaga
{
    OrcamentoAprovado,
    ExecucaoLiberada,
    ExecucaoConcluida,
    IntervencaoRequerida,
    PagamentoCapturado,
    Compensando,
    Compensada,
    CompensacaoFalhou,
    /// <summary>Reserva de insumos pedida à Execução e ainda sem resposta (passo pendente: aplica a trava 409 <c>saga_em_andamento</c>).</summary>
    ReservandoInsumos,
    /// <summary>Sem saldo (D-20): nada foi reservado, o orçamento não é compensado; a equipe retenta ou cancela.</summary>
    AguardandoInsumos
}

public enum SituacaoEtapa
{
    Pendente,
    Concluida,
    Falhou,
    Compensando,
    Compensada,
    CompensacaoFalhou
}

/// <summary>Os sete valores de status de pagamento do Billing; só <see cref="Capturado"/> muda a etapa da Saga.</summary>
public enum StatusPagamento
{
    SemTentativa,
    Pendente,
    Capturado,
    Rejeitado,
    Cancelado,
    Expirado,
    Reembolsado
}

public enum DestinoComando
{
    Execucao,
    Billing,
    /// <summary>Lembretes de negócio (<c>saga.lembrete.v1</c>), entregues ao Cadastros para notificar o cliente.</summary>
    Cadastros
}
