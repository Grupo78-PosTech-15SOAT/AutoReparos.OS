using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.OS.Application.Saga;

/// <summary>Consome a fila <c>Sqs:SagaQueueUrl</c>. A mensagem só é apagada após processada ou enviada à DLQ.</summary>
public sealed class SagaConsumidor(IServiceProvider services, IConfiguration configuration, ILogger<SagaConsumidor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var fila = configuration["Sqs:SagaQueueUrl"];
        if (string.IsNullOrWhiteSpace(fila))
        {
            logger.LogError("Sqs:SagaQueueUrl não configurada; o consumidor da Saga não será iniciado.");
            return;
        }

        var sqs = services.GetRequiredService<IAmazonSQS>();
        var processador = services.GetRequiredService<ProcessadorMensagemSaga>();
        var falhas = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var resposta = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = fila, MaxNumberOfMessages = 10, WaitTimeSeconds = 20 }, stoppingToken);
                foreach (var mensagem in resposta.Messages ?? [])
                {
                    try
                    {
                        await processador.ProcessarAsync(mensagem.Body, stoppingToken);
                        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = fila, ReceiptHandle = mensagem.ReceiptHandle }, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // Ex.: DLQ indisponível. Sem Delete, a mensagem reaparece após o timeout de visibilidade.
                        logger.LogError(ex, "Mensagem {MessageId} da Saga não pôde ser concluída nem enviada à DLQ; permanece na fila. Intervenção necessária se persistir.", mensagem.MessageId);
                    }
                }
                falhas = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                falhas = Math.Min(falhas + 1, 6);
                logger.LogError(ex, "Falha ao ler a fila da Saga.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, falhas) + Random.Shared.NextDouble()), stoppingToken);
            }
        }
    }
}
