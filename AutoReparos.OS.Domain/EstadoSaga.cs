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

public enum StatusPagamento
{
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
