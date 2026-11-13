using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;

/// <summary>
/// Reprocessa a DLQ da Saga (<c>Sqs:SagaDeadLetterQueueUrl</c>): devolve cada mensagem à fila de origem, escolhida pelo <c>tipo</c>
/// (eventos de entrada voltam à fila da Saga; comandos esgotados voltam à fila do Execution, do Billing ou do Cadastros), e só a apaga
/// da DLQ depois de reenviada. Mensagem de tipo desconhecido permanece na DLQ. Cada ação é registrada com o autor (alerta
/// <c>saga.dlq-reprocessada</c>).
/// </summary>
public sealed class ReprocessadorDlq(IAmazonSQS sqs, IConfiguration configuration, IAlertador alertador)
{
    public const int QuantidadeMaxima = 100;

    public async Task<ResultadoReprocessamentoDlq> ReprocessarAsync(int quantidade, string autor, CancellationToken cancellationToken)
    {
        var dlq = Exigir("Sqs:SagaDeadLetterQueueUrl");
        int recebidas = 0, reprocessadas = 0, ignoradas = 0;
        while (recebidas < quantidade)
        {
            var resposta = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = dlq, MaxNumberOfMessages = Math.Min(10, quantidade - recebidas), WaitTimeSeconds = 0 }, cancellationToken);
            if (resposta.Messages is not { Count: > 0 } mensagens) break;
            foreach (var mensagem in mensagens)
            {
                recebidas++;
                if (ChaveDestino(mensagem.Body) is not { } chave) { ignoradas++; continue; }
                await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = Exigir(chave), MessageBody = mensagem.Body }, cancellationToken);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = dlq, ReceiptHandle = mensagem.ReceiptHandle }, cancellationToken);
                reprocessadas++;
            }
        }
        alertador.Alertar("saga.dlq-reprocessada", $"DLQ da Saga reprocessada por {autor}: {reprocessadas} de {quantidade} solicitadas devolvidas à fila de origem; {ignoradas} de tipo desconhecido permaneceram.");
        return new ResultadoReprocessamentoDlq(quantidade, reprocessadas, ignoradas);
    }

    private static string? ChaveDestino(string? corpo)
    {
        if (!TentarLerTipo(corpo, out var tipo)) return null;
        if (TiposMensagem.Entrada.Contains(tipo)) return "Sqs:SagaQueueUrl";
        return TiposMensagem.NormalizarComando(tipo) switch
        {
            TiposMensagem.OrdemServicoLiberada or TiposMensagem.ExecucaoCancelar or TiposMensagem.ExecucaoReservarInsumos or TiposMensagem.ExecucaoCancelarReserva => "Sqs:ExecucaoQueueUrl",
            TiposMensagem.CobrancaCancelarOrcamento => "Sqs:BillingQueueUrl",
            TiposMensagem.SagaLembrete => "Sqs:CadastrosQueueUrl",
            _ => null
        };
    }

    private static bool TentarLerTipo(string? corpo, out string tipo)
    {
        tipo = string.Empty;
        if (string.IsNullOrWhiteSpace(corpo)) return false;
        try
        {
            using var documento = JsonDocument.Parse(corpo);
            if (documento.RootElement.ValueKind != JsonValueKind.Object || !documento.RootElement.TryGetProperty("tipo", out var elemento) || elemento.ValueKind != JsonValueKind.String) return false;
            tipo = elemento.GetString() ?? string.Empty;
            return tipo.Length > 0;
        }
        catch (JsonException) { return false; }
    }

    private string Exigir(string chave) =>
        configuration[chave] is { Length: > 0 } valor ? valor : throw new InvalidOperationException($"{chave} não configurada.");
}

public sealed record ResultadoReprocessamentoDlq(int Solicitadas, int Reprocessadas, int Ignoradas);
