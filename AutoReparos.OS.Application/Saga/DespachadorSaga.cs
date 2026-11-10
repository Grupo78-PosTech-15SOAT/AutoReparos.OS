namespace AutoReparos.OS.Application.Saga;

/// <summary>Ciclo do despachador: retoma Sagas com trabalho pendente devido (inclusive após reinício do processo).</summary>
public sealed class DespachadorSaga(ISagaRepositorio sagas, OrquestradorSaga orquestrador, IAlertador alertador, TimeProvider tempo)
{
    /// <summary>
    /// Progride cada Saga com trabalho devido; devolve quantas foram visitadas. Uma exceção em uma Saga é alertada e não
    /// interrompe o ciclo das demais (o trabalho dela continua persistido para o próximo ciclo).
    /// </summary>
    public async Task<int> ExecutarCicloAsync(CancellationToken cancellationToken)
    {
        var ids = await sagas.ListarComTrabalhoPendenteAsync(tempo.GetUtcNow(), cancellationToken);
        foreach (var id in ids)
        {
            try
            {
                await orquestrador.ProgredirAsync(id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                alertador.Alertar("saga.despacho-falhou", $"Falha ao progredir a Saga da OS {id}: {ex.Message.TrimEnd('.')}. As demais Sagas seguem; o trabalho permanece persistido e será retomado no próximo ciclo.", id);
            }
        }
        return ids.Count;
    }
}
