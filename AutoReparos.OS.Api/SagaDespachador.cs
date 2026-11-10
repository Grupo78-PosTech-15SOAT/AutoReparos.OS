using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Infrastructure.Saga;

/// <summary>
/// Retoma e publica o trabalho pendente das Sagas (comandos persistidos e sincronizações da OS), inclusive ao iniciar
/// o processo. Cria as tabelas se <c>DynamoDb:CriarTabelas</c> for verdadeiro (ambiente local e de teste).
/// </summary>
public sealed class SagaDespachador(IServiceProvider services, IConfiguration configuration, ILogger<SagaDespachador> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(configuration["Sqs:SagaQueueUrl"]))
        {
            logger.LogError("Sqs:SagaQueueUrl não configurada; o despachador da Saga não será iniciado.");
            return;
        }

        var falhas = 0;
        var inicializado = !configuration.GetValue("DynamoDb:CriarTabelas", false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!inicializado)
                {
                    await services.GetRequiredService<DynamoDbInicializador>().GarantirTabelasAsync(stoppingToken);
                    inicializado = true;
                }
                var visitadas = await services.GetRequiredService<DespachadorSaga>().ExecutarCicloAsync(stoppingToken);
                falhas = 0;
                await Task.Delay(TimeSpan.FromSeconds(visitadas == 0 ? 2 : 1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                falhas = Math.Min(falhas + 1, 6);
                logger.LogError(ex, "Falha no ciclo do despachador da Saga; o trabalho pendente permanece persistido.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, falhas) + Random.Shared.NextDouble()), stoppingToken);
            }
        }
    }
}
