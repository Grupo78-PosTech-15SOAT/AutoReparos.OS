namespace AutoReparos.OS.Application.Saga;

/// <summary>Ciclo do despachador: retoma Sagas com trabalho pendente devido (inclusive após reinício do processo).</summary>
public sealed class DespachadorSaga(ISagaRepositorio sagas, OrquestradorSaga orquestrador, TimeProvider tempo)
{
    /// <summary>Progride cada Saga com trabalho devido; devolve quantas foram visitadas.</summary>
    public async Task<int> ExecutarCicloAsync(CancellationToken cancellationToken)
    {
        var ids = await sagas.ListarComTrabalhoPendenteAsync(tempo.GetUtcNow(), cancellationToken);
        foreach (var id in ids) await orquestrador.ProgredirAsync(id, cancellationToken);
        return ids.Count;
    }
}
