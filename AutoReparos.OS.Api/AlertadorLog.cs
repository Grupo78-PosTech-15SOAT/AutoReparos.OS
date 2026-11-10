using AutoReparos.OS.Application.Saga;

/// <summary>Alerta acionável: log de erro estruturado com o código; o ambiente de observabilidade alerta pelo código.</summary>
public sealed class AlertadorLog(ILogger<AlertadorLog> logger) : IAlertador
{
    public void Alertar(string codigo, string mensagem, Guid? ordemServicoId = null, Guid? eventId = null) =>
        logger.LogError("ALERTA {CodigoAlerta} OrdemServicoId={OrdemServicoId} EventId={EventId}: {Mensagem}", codigo, ordemServicoId, eventId, mensagem);
}
