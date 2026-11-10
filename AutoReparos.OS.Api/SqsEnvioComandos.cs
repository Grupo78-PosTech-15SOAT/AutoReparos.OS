using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;

/// <summary>Publica comandos da Saga nas filas do Execution e do Billing e envia mensagens esgotadas à DLQ da Saga.</summary>
public sealed class SqsEnvioComandos(IAmazonSQS sqs, IConfiguration configuration) : IEnvioComandos
{
    public Task EnviarAsync(DestinoComando destino, string corpo, CancellationToken cancellationToken)
    {
        var chave = destino == DestinoComando.Execucao ? "Sqs:ExecucaoQueueUrl" : "Sqs:BillingQueueUrl";
        return sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = Exigir(chave), MessageBody = corpo }, cancellationToken);
    }

    public Task EnviarParaDlqAsync(string corpo, string motivo, CancellationToken cancellationToken)
    {
        var requisicao = new SendMessageRequest { QueueUrl = Exigir("Sqs:SagaDeadLetterQueueUrl"), MessageBody = corpo };
        requisicao.MessageAttributes["motivo"] = new MessageAttributeValue { DataType = "String", StringValue = motivo.Length > 1000 ? motivo[..1000] : motivo };
        return sqs.SendMessageAsync(requisicao, cancellationToken);
    }

    private string Exigir(string chave) =>
        configuration[chave] is { Length: > 0 } valor ? valor : throw new InvalidOperationException($"{chave} não configurada.");
}
