using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.OS.Infrastructure;
using Microsoft.EntityFrameworkCore;

public sealed class PublicadorSqs(IServiceScopeFactory scopes, IAmazonSQS sqs, IConfiguration configuration, ILogger<PublicadorSqs> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueUrl = configuration["Sqs:QueueUrl"];
        if (string.IsNullOrWhiteSpace(queueUrl))
        {
            logger.LogError("Sqs:QueueUrl não configurada; eventos permanecerão no outbox.");
            return;
        }

        var falhas = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
                var mensagem = await db.MensagensPendentes
                    .FromSqlRaw("SELECT * FROM mensagens_pendentes WHERE \"EnviadoEm\" IS NULL AND (\"ProximaTentativaEm\" IS NULL OR \"ProximaTentativaEm\" <= now()) ORDER BY \"CriadoEm\", \"Id\" LIMIT 1 FOR UPDATE SKIP LOCKED")
                    .FirstOrDefaultAsync(stoppingToken);
                if (mensagem is not null)
                {
                    var dlq = mensagem.Tentativas >= 5;
                    try
                    {
                        var destino = dlq ? configuration["Sqs:DeadLetterQueueUrl"] : queueUrl;
                        if (string.IsNullOrWhiteSpace(destino)) throw new InvalidOperationException("Sqs:DeadLetterQueueUrl não configurada.");
                        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = destino, MessageBody = mensagem.Conteudo }, stoppingToken);
                        mensagem.EnviadoEm = DateTimeOffset.UtcNow;
                        mensagem.EncaminhadoParaDlq = dlq;
                        if (dlq) logger.LogError("Evento {EventId} encaminhado à DLQ após {Tentativas} tentativas; intervenção necessária.", mensagem.Id, mensagem.Tentativas);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        mensagem.Tentativas++;
                        var atraso = Math.Pow(2, Math.Min(mensagem.Tentativas, 6)) + Random.Shared.NextDouble();
                        mensagem.ProximaTentativaEm = DateTimeOffset.UtcNow.AddSeconds(atraso);
                        logger.LogError(ex, "Falha ao enviar evento {EventId}; tentativa {Tentativas}. Intervenção necessária se persistir.", mensagem.Id, mensagem.Tentativas);
                    }
                    await db.SaveChangesAsync(stoppingToken);
                }
                await transaction.CommitAsync(stoppingToken);
                falhas = 0;
                if (mensagem is null) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                falhas = Math.Min(falhas + 1, 6);
                logger.LogError(ex, "Falha ao publicar evento SQS; o evento permanece no outbox.");
                var delay = TimeSpan.FromSeconds(Math.Pow(2, falhas) + Random.Shared.NextDouble());
                await Task.Delay(delay, stoppingToken);
            }
        }
    }
}
