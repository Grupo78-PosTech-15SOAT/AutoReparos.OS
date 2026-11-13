using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.OS.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>C12 de saga-compensacoes no OS: <c>POST /api/v1/admin/dlq/reprocessar</c> devolve as mensagens da DLQ às filas de origem.</summary>
public sealed class SagaDlqRotaTests(SagaAmbiente ambiente) : SagaRotaBase(ambiente)
{
    private const string Dlq = "dlq-saga", FilaSaga = "fila-saga", FilaExecucao = "fila-execucao", FilaBilling = "fila-billing", FilaCadastros = "fila-cadastros";

    private sealed class SqsDlqFalso() : AmazonSQSClient(new AnonymousAWSCredentials(), new AmazonSQSConfig { RegionEndpoint = RegionEndpoint.USEast1 })
    {
        public Queue<Message> NaDlq { get; } = new();
        public List<(string Fila, string Corpo)> Enviadas { get; } = [];
        public List<string> Apagadas { get; } = [];

        public override Task<ReceiveMessageResponse> ReceiveMessageAsync(ReceiveMessageRequest request, CancellationToken cancellationToken = default)
        {
            var lote = new List<Message>();
            // O consumidor em segundo plano da Saga também usa este cliente: só a DLQ entrega mensagens.
            if (request.QueueUrl == Dlq) while (NaDlq.Count > 0 && lote.Count < request.MaxNumberOfMessages) lote.Add(NaDlq.Dequeue());
            return Task.FromResult(new ReceiveMessageResponse { Messages = lote });
        }

        public override Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add((request.QueueUrl, request.MessageBody));
            return Task.FromResult(new SendMessageResponse { MessageId = "novo" });
        }

        public override Task<DeleteMessageResponse> DeleteMessageAsync(DeleteMessageRequest request, CancellationToken cancellationToken = default)
        {
            Apagadas.Add(request.ReceiptHandle);
            return Task.FromResult(new DeleteMessageResponse());
        }
    }

    private static Message Mensagem(string tipo, string recibo) => new() { ReceiptHandle = recibo, Body = $"{{\"eventId\":\"{Guid.NewGuid()}\",\"tipo\":\"{tipo}\"}}" };

    private (HttpClient Cliente, SqsDlqFalso Sqs) ClienteComSqs(string papel)
    {
        var sqs = new SqsDlqFalso();
        var fabrica = Fabrica.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sqs:SagaDeadLetterQueueUrl"] = Dlq,
                ["Sqs:SagaQueueUrl"] = FilaSaga,
                ["Sqs:ExecucaoQueueUrl"] = FilaExecucao,
                ["Sqs:BillingQueueUrl"] = FilaBilling,
                ["Sqs:CadastrosQueueUrl"] = FilaCadastros
            }));
            builder.ConfigureServices(services => services.AddSingleton<IAmazonSQS>(sqs));
        });
        var cliente = fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add("X-Test-Role", papel);
        return (cliente, sqs);
    }

    [Fact]
    public async Task ReprocessaDlqDoOs_DevolveCadaMensagemAFilaDeOrigemAteAQuantidadePedida()
    {
        var (cliente, sqs) = ClienteComSqs("Administrador");
        using var _ = cliente;
        foreach (var (tipo, recibo) in new[]
        {
            (TiposMensagem.ExecucaoConcluida, "r1"), (TiposMensagem.ExecucaoCancelarReserva, "r2"), (TiposMensagem.CobrancaCancelarOrcamento, "r3"),
            (TiposMensagem.SagaLembrete, "r4"), ("tipo.desconhecido.v1", "r5"), (TiposMensagem.InsumosReservados, "r6")
        }) sqs.NaDlq.Enqueue(Mensagem(tipo, recibo));

        var resposta = await cliente.PostAsJsonAsync("/api/v1/admin/dlq/reprocessar", new { quantidade = 5 });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((5, 4, 1), (corpo.GetProperty("solicitadas").GetInt32(), corpo.GetProperty("reprocessadas").GetInt32(), corpo.GetProperty("ignoradas").GetInt32()));
        Assert.Equal([FilaSaga, FilaExecucao, FilaBilling, FilaCadastros], sqs.Enviadas.Select(e => e.Fila));
        Assert.Equal(["r1", "r2", "r3", "r4"], sqs.Apagadas);
        // A sexta mensagem passou do limite pedido e continua na DLQ.
        Assert.Single(sqs.NaDlq);
        var alerta = Assert.Single(Alertas.Alertas);
        Assert.Equal("saga.dlq-reprocessada", alerta.Codigo);
        Assert.Contains("4 de 5", alerta.Mensagem);
    }

    [Theory]
    [InlineData("Atendente", "{\"quantidade\":1}", HttpStatusCode.Forbidden)]
    [InlineData("Administrador", "{\"quantidade\":0}", HttpStatusCode.UnprocessableEntity)]
    [InlineData("Administrador", "{\"quantidade\":101}", HttpStatusCode.UnprocessableEntity)]
    [InlineData("Administrador", "{}", HttpStatusCode.UnprocessableEntity)]
    public async Task ReprocessaDlqDoOs_RecusaPapelEQuantidadeInvalidos(string papel, string corpo, HttpStatusCode esperado)
    {
        var (cliente, sqs) = ClienteComSqs(papel);
        using var _ = cliente;
        sqs.NaDlq.Enqueue(Mensagem(TiposMensagem.ExecucaoConcluida, "r1"));

        var resposta = await Enviar(cliente, HttpMethod.Post, "/api/v1/admin/dlq/reprocessar", corpo);

        Assert.Equal(esperado, resposta.StatusCode);
        Assert.Empty(sqs.Enviadas);
        Assert.Single(sqs.NaDlq);
    }
}
