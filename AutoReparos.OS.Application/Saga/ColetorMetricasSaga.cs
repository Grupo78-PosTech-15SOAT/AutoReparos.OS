namespace AutoReparos.OS.Application.Saga;

/// <summary>Lê a profundidade da DLQ da Saga (<c>ApproximateNumberOfMessages</c>).</summary>
public interface IProfundidadeDlqSaga
{
    Task<long> LerAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Atualiza os gauges da Saga (D-66). Sagas paradas seguem a regra do console de Sagas: não terminais e sem atualização
/// há pelo menos <see cref="OpcoesSaga.LimiarParada"/>. Uma falha de leitura retira a amostra do gauge e não propaga.
/// </summary>
public sealed class ColetorMetricasSaga(ISagaRepositorio sagas, IProfundidadeDlqSaga dlq, TimeProvider tempo, OpcoesSaga opcoes)
{
    public async Task ColetarAsync(CancellationToken cancellationToken)
    {
        try
        {
            var inativas = await sagas.ListarAsync(null, tempo.GetUtcNow() - opcoes.LimiarParada, cancellationToken);
            TelemetriaSaga.AtualizarParadas(inativas.Count(saga => !saga.Terminal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TelemetriaSaga.AtualizarParadas(null);
        }

        try
        {
            TelemetriaSaga.AtualizarDlq(await dlq.LerAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TelemetriaSaga.AtualizarDlq(null);
        }
    }
}
