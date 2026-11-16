using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

/// <summary>Consumidor SQS da Saga: visibilidade estendida antes de processar e processamento com tempo limitado.</summary>
public sealed class SagaConsumidorTests(SagaAmbiente ambiente) : SagaTestBase(ambiente)
{
    private const string Fila = "http://sqs.local/saga";

    private static SagaConsumidor NovoConsumidor(string? limite = null)
    {
        var valores = new Dictionary<string, string?>();
        if (limite is not null) valores["Saga:LimiteProcessamentoMensagem"] = limite;
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        return new SagaConsumidor(new ServiceCollection().BuildServiceProvider(), configuracao, NullLogger<SagaConsumidor>.Instance);
    }

    private static Mock<IAmazonSQS> SqsFalso(List<string> chamadas)
    {
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(s => s.ChangeMessageVisibilityAsync(It.IsAny<ChangeMessageVisibilityRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChangeMessageVisibilityRequest, CancellationToken>((r, _) => chamadas.Add($"visibilidade:{r.VisibilityTimeout}"))
            .ReturnsAsync(new ChangeMessageVisibilityResponse());
        sqs.Setup(s => s.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DeleteMessageRequest, CancellationToken>((r, _) => chamadas.Add($"apagar:{r.ReceiptHandle}"))
            .ReturnsAsync(new DeleteMessageResponse());
        return sqs;
    }

    private static Message MensagemSqs(string corpo) => new() { MessageId = "m-1", ReceiptHandle = "recibo-1", Body = corpo };

    [Fact]
    public async Task SagaConsumer_ExtendsTheVisibilityBeforeProcessingAndDeletesOnlyAfterTheMessageIsProcessed()
    {
        var chamadas = new List<string>();
        var sqs = SqsFalso(chamadas);
        var (id, _, _) = await CriarOSAsync();
        var (corpo, _) = Mensagem(TiposMensagem.OrcamentoAprovado, id, Guid.NewGuid(), null, ("orcamentoId", Guid.NewGuid()), ("clienteId", Guid.NewGuid()), ("itens", Array.Empty<object>()));
        Atual.Envio.AoEnviar = (_, _) => { chamadas.Add("envio"); return Task.CompletedTask; };

        await NovoConsumidor().ProcessarMensagemAsync(sqs.Object, Fila, Atual.Processador, MensagemSqs(corpo), CancellationToken.None);

        // Limite padrão de 60 s: a visibilidade vai a 120 s antes do processamento; a exclusão vem depois do envio do comando.
        Assert.Equal(["visibilidade:120", "envio", "apagar:recibo-1"], chamadas);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, (await SagaAsync(id)).Estado);
        sqs.Verify(s => s.ChangeMessageVisibilityAsync(It.Is<ChangeMessageVisibilityRequest>(r => r.QueueUrl == Fila && r.ReceiptHandle == "recibo-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SagaConsumer_MessageThatExceedsTheProcessingLimitIsNotDeletedAndStaysInTheQueue()
    {
        var chamadas = new List<string>();
        var sqs = SqsFalso(chamadas);
        // Falha transitória (Saga inexistente) com espera que só termina quando o limite cancela o processamento.
        var bloqueante = new ProcessadorMensagemSaga(Atual.Orquestrador, Atual.Envio, Atual.Alertas,
            new OpcoesSaga { Aguardar = (_, ct) => Task.Delay(Timeout.Infinite, ct) });
        var (corpo, _) = Mensagem(TiposMensagem.ExecucaoConcluida, Guid.NewGuid());
        var cronometro = System.Diagnostics.Stopwatch.StartNew();

        await NovoConsumidor("00:00:00.300").ProcessarMensagemAsync(sqs.Object, Fila, bloqueante, MensagemSqs(corpo), CancellationToken.None);

        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(10), $"o limite deveria interromper o processamento (levou {cronometro.Elapsed})");
        Assert.Equal(["visibilidade:1"], chamadas);
        sqs.Verify(s => s.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(Atual.Envio.Dlq);
        Assert.Empty(Atual.Alertas.Alertas);
    }

    [Fact]
    public async Task SagaConsumer_FailureToExtendTheVisibilityDoesNotPreventProcessingAndDeletion()
    {
        var chamadas = new List<string>();
        var sqs = SqsFalso(chamadas);
        sqs.Setup(s => s.ChangeMessageVisibilityAsync(It.IsAny<ChangeMessageVisibilityRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonSQSException("recibo expirado"));
        var (id, _, _) = await CriarOSAsync();
        var (corpo, _) = Mensagem(TiposMensagem.OrcamentoAprovado, id, null, null, ("orcamentoId", Guid.NewGuid()), ("clienteId", Guid.NewGuid()), ("itens", Array.Empty<object>()));

        await NovoConsumidor().ProcessarMensagemAsync(sqs.Object, Fila, Atual.Processador, MensagemSqs(corpo), CancellationToken.None);

        Assert.Equal(["apagar:recibo-1"], chamadas);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, (await SagaAsync(id)).Estado);
    }

    [Fact]
    public async Task SagaConsumer_MessageSentToTheDeadLetterQueueIsDeletedButADeadLetterFailureKeepsItInTheQueue()
    {
        var chamadas = new List<string>();
        var sqs = SqsFalso(chamadas);
        var consumidor = NovoConsumidor();

        await consumidor.ProcessarMensagemAsync(sqs.Object, Fila, Atual.Processador, MensagemSqs("lixo"), CancellationToken.None);
        Assert.Equal(["visibilidade:120", "apagar:recibo-1"], chamadas);
        Assert.Single(Atual.Envio.Dlq);

        chamadas.Clear();
        Atual.Envio.FalhaDlq = () => new IOException("DLQ indisponível");
        await consumidor.ProcessarMensagemAsync(sqs.Object, Fila, Atual.Processador, MensagemSqs("lixo"), CancellationToken.None);
        Assert.Equal(["visibilidade:120"], chamadas);
    }

    [Fact]
    public async Task SagaConsumer_ShutdownPropagatesInsteadOfBeingTreatedAsATimeout()
    {
        var chamadas = new List<string>();
        var sqs = SqsFalso(chamadas);
        var bloqueante = new ProcessadorMensagemSaga(Atual.Orquestrador, Atual.Envio, Atual.Alertas,
            new OpcoesSaga { Aguardar = (_, ct) => Task.Delay(Timeout.Infinite, ct) });
        var (corpo, _) = Mensagem(TiposMensagem.ExecucaoConcluida, Guid.NewGuid());
        using var parada = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NovoConsumidor().ProcessarMensagemAsync(sqs.Object, Fila, bloqueante, MensagemSqs(corpo), parada.Token));

        Assert.DoesNotContain(chamadas, c => c.StartsWith("apagar", StringComparison.Ordinal));
    }
}
