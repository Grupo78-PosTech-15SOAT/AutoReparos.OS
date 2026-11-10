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
    CompensacaoFalhou
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
    Billing
}
