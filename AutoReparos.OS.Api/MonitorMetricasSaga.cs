using Amazon.SQS;
using AutoReparos.OS.Application.Saga;

namespace AutoReparos.OS.Api;

/// <summary>Profundidade da DLQ da Saga lida da SQS (<c>Sqs:SagaDeadLetterQueueUrl</c>).</summary>
public sealed class SqsProfundidadeDlqSaga(IAmazonSQS sqs, IConfiguration configuration) : IProfundidadeDlqSaga
{
    public async Task<long> LerAsync(CancellationToken cancellationToken)
    {
        var fila = configuration["Sqs:SagaDeadLetterQueueUrl"];
        if (string.IsNullOrWhiteSpace(fila)) throw new InvalidOperationException("Sqs:SagaDeadLetterQueueUrl não configurada.");
        var resposta = await sqs.GetQueueAttributesAsync(fila, ["ApproximateNumberOfMessages"], cancellationToken);
        return resposta.ApproximateNumberOfMessages;
    }
}

/// <summary>Atualiza os gauges da Saga a cada 30 segundos; o exportador de métricas só lê o último valor.</summary>
public sealed class MonitorMetricasSaga(IServiceProvider services, IConfiguration configuration, ILogger<MonitorMetricasSaga> logger) : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(configuration["Sqs:SagaQueueUrl"]))
        {
            logger.LogWarning("Sqs:SagaQueueUrl não configurada; os gauges da Saga não serão coletados.");
            return;
        }

        var coletor = services.GetRequiredService<ColetorMetricasSaga>();
        while (!stoppingToken.IsCancellationRequested)
        {
            await coletor.ColetarAsync(stoppingToken);
            try { await Task.Delay(Intervalo, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
