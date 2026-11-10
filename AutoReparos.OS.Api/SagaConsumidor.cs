using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.OS.Application.Saga;

/// <summary>
/// Consome a fila <c>Sqs:SagaQueueUrl</c>. A mensagem só é apagada após processada ou enviada à DLQ. Cada mensagem é
/// processada com tempo limitado (<c>Saga:LimiteProcessamentoMensagem</c>, padrão 60 s) e, antes de processar, o
/// visibility timeout dela é estendido para o dobro do limite: mensagens de um lote que esperam a vez não reaparecem na
/// fila enquanto são atendidas. Estourado o limite, a mensagem não é apagada e volta à fila quando a visibilidade vence.
/// </summary>
public sealed class SagaConsumidor(IServiceProvider services, IConfiguration configuration, ILogger<SagaConsumidor> logger) : BackgroundService
{
    public static readonly TimeSpan LimitePadrao = TimeSpan.FromSeconds(60);

    public TimeSpan LimiteProcessamento => configuration.GetValue<TimeSpan?>("Saga:LimiteProcessamentoMensagem") is { } limite && limite > TimeSpan.Zero ? limite : LimitePadrao;

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
                    await ProcessarMensagemAsync(sqs, fila, processador, mensagem, stoppingToken);
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

    /// <summary>Estende a visibilidade, processa com tempo limitado e apaga a mensagem somente se concluída (ou enviada à DLQ).</summary>
    public async Task ProcessarMensagemAsync(IAmazonSQS sqs, string fila, ProcessadorMensagemSaga processador, Message mensagem, CancellationToken stoppingToken)
    {
        var limite = LimiteProcessamento;
        try
        {
            await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
            {
                QueueUrl = fila,
                ReceiptHandle = mensagem.ReceiptHandle,
                VisibilityTimeout = (int)Math.Ceiling(limite.TotalSeconds * 2)
            }, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Não foi possível estender a visibilidade da mensagem {MessageId} da Saga; o processamento segue com o timeout original.", mensagem.MessageId);
        }

        using var limitado = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        limitado.CancelAfter(limite);
        try
        {
            await processador.ProcessarAsync(mensagem.Body, limitado.Token);
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = fila, ReceiptHandle = mensagem.ReceiptHandle }, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            logger.LogError("Mensagem {MessageId} da Saga excedeu o limite de {Limite} de processamento; permanece na fila e reaparece quando a visibilidade vencer.", mensagem.MessageId, limite);
        }
        catch (Exception ex)
        {
            // Ex.: DLQ indisponível. Sem Delete, a mensagem reaparece após o timeout de visibilidade.
            logger.LogError(ex, "Mensagem {MessageId} da Saga não pôde ser concluída nem enviada à DLQ; permanece na fila. Intervenção necessária se persistir.", mensagem.MessageId);
        }
    }
}
