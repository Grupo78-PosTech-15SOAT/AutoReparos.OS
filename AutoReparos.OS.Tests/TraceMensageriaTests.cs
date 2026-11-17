using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

/// <summary>Contexto de trace W3C nas mensagens da Saga: gravado no comando, publicado em span Producer e continuado em span Consumer.</summary>
public sealed class TraceMensageriaTests : SagaTestBase, IDisposable
{
    private const string Fila = "http://sqs.local/saga";
    private const string FonteMensageria = "AutoReparos.OS.Mensageria";
    private const string TraceT = "0af7651916cd43dd8448eb211c80319c";
    private const string SpanT = "b7ad6b7169203331";
    private const string TraceparentT = $"00-{TraceT}-{SpanT}-01";

    private const string NomeFonteTeste = "AutoReparos.OS.Tests.Trace";
    private static readonly ActivitySource FonteTeste = new(NomeFonteTeste);
    private readonly List<Activity> encerradas = [];
    private readonly ActivityListener ouvinte;

    public TraceMensageriaTests(SagaAmbiente ambiente) : base(ambiente)
    {
        ouvinte = new ActivityListener
        {
            ShouldListenTo = fonte => fonte.Name == FonteMensageria || fonte.Name == NomeFonteTeste,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = atividade => { lock (encerradas) encerradas.Add(atividade); }
        };
        ActivitySource.AddActivityListener(ouvinte);
    }

    public void Dispose() => ouvinte.Dispose();

    /// <summary>Spans da mensageria desta OS (o ouvinte é global ao processo; outros testes podem emitir spans em paralelo).</summary>
    private List<Activity> Spans(ActivityKind kind, Guid ordemServicoId)
    {
        lock (encerradas)
            return encerradas.Where(a => a.Source.Name == FonteMensageria && a.Kind == kind && (string?)a.GetTagItem("ordem_servico.id") == ordemServicoId.ToString()).ToList();
    }

    private static SagaConsumidor NovoConsumidor() =>
        new(new ServiceCollection().BuildServiceProvider(), new ConfigurationBuilder().Build(), NullLogger<SagaConsumidor>.Instance);

    private static Mock<IAmazonSQS> SqsFalso(List<SendMessageRequest>? enviadas = null)
    {
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(s => s.ChangeMessageVisibilityAsync(It.IsAny<ChangeMessageVisibilityRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChangeMessageVisibilityResponse());
        sqs.Setup(s => s.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new DeleteMessageResponse());
        sqs.Setup(s => s.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SendMessageRequest, CancellationToken>((r, _) => enviadas?.Add(r))
            .ReturnsAsync(new SendMessageResponse());
        return sqs;
    }

    private Task ConsumirAsync(Mock<IAmazonSQS> sqs, string corpo) =>
        NovoConsumidor().ProcessarMensagemAsync(sqs.Object, Fila, Atual.Processador, new Message { MessageId = "m-1", ReceiptHandle = "recibo-1", Body = corpo }, CancellationToken.None);

    private string Aprovacao(Guid ordemServicoId, params (string Chave, object? Valor)[] extras)
    {
        (string, object?)[] campos = [("orcamentoId", Guid.NewGuid()), ("clienteId", Guid.NewGuid()), ("itens", Array.Empty<object>()), .. extras];
        return Mensagem(TiposMensagem.OrcamentoAprovado, ordemServicoId, Guid.NewGuid(), null, campos).Corpo;
    }

    [Fact]
    public async Task ComandoCriadoComActivityCorrente_GravaTraceparentETracestateDaActivityNoCorpo()
    {
        var (id, _, _) = await CriarOSAsync();
        string idDaActivity;
        using (var atividade = FonteTeste.StartActivity("requisicao de teste"))
        {
            Assert.NotNull(atividade);
            atividade.TraceStateString = "fornecedor=valor-1";
            idDaActivity = Activity.Current!.Id!;

            await Atual.Processador.ProcessarAsync(Aprovacao(id), CancellationToken.None);
        }

        var comando = Assert.Single((await SagaAsync(id)).Comandos);
        var corpo = Json.Ler(comando.Corpo);
        Assert.Equal(ActivityIdFormat.W3C, Activity.DefaultIdFormat);
        Assert.Equal(idDaActivity, corpo.GetProperty("traceparent").GetString());
        Assert.Equal("fornecedor=valor-1", corpo.GetProperty("tracestate").GetString());
        // O corpo publicado é o mesmo gravado: o contexto viaja dentro do envelope.
        Assert.Equal(comando.Corpo, Assert.Single(Atual.Envio.Enviados).Corpo);
    }

    [Fact]
    public async Task ComandoCriadoSemActivityCorrente_NaoTemTraceparentEEGravado()
    {
        var (id, _, _) = await CriarOSAsync();
        Assert.Null(Activity.Current);

        await Atual.Processador.ProcessarAsync(Aprovacao(id), CancellationToken.None);

        var comando = Assert.Single((await SagaAsync(id)).Comandos);
        Assert.Equal(TiposMensagem.OrdemServicoLiberada, comando.Tipo);
        Assert.DoesNotContain("traceparent", Json.Chaves(comando.Corpo));
        Assert.DoesNotContain("tracestate", Json.Chaves(comando.Corpo));
    }

    [Fact]
    public async Task PublicarComandoComTraceparent_RegistraSpanProducerPublicarTipoComOMesmoTraceId()
    {
        var ordemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var corpo = Mensagem(TiposMensagem.OrdemServicoLiberada, ordemId, null, eventId, ("traceparent", TraceparentT)).Corpo;
        var enviadas = new List<SendMessageRequest>();
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sqs:ExecucaoQueueUrl"] = "http://sqs.local/execucao" }).Build();

        await new SqsEnvioComandos(SqsFalso(enviadas).Object, configuracao).EnviarAsync(DestinoComando.Execucao, corpo, CancellationToken.None);

        var span = Assert.Single(Spans(ActivityKind.Producer, ordemId));
        Assert.Equal("publicar ordem-servico.liberada.v1", span.OperationName);
        Assert.Equal(FonteMensageria, span.Source.Name);
        Assert.Equal(TraceT, span.TraceId.ToHexString());
        Assert.Equal(SpanT, span.ParentSpanId.ToHexString());
        Assert.Equal("aws_sqs", span.GetTagItem("messaging.system"));
        Assert.Equal(eventId.ToString(), span.GetTagItem("messaging.message.id"));
        var requisicao = Assert.Single(enviadas);
        Assert.Equal("http://sqs.local/execucao", requisicao.QueueUrl);
        Assert.Equal(corpo, requisicao.MessageBody);
    }

    [Fact]
    public async Task ConsumirMensagemComTraceparentValido_ProcessaEmSpanConsumerFilhoDoEnvelopeComOrdemServicoId()
    {
        var (id, _, _) = await CriarOSAsync();
        var sqs = SqsFalso();

        await ConsumirAsync(sqs, Aprovacao(id, ("traceparent", TraceparentT)));

        var span = Assert.Single(Spans(ActivityKind.Consumer, id));
        Assert.Equal("processar orcamento.aprovado.v1", span.OperationName);
        Assert.Equal(FonteMensageria, span.Source.Name);
        Assert.Equal(TraceT, span.TraceId.ToHexString());
        Assert.Equal(SpanT, span.ParentSpanId.ToHexString());
        Assert.True(span.HasRemoteParent);
        Assert.Equal(id.ToString(), span.GetTagItem("ordem_servico.id"));
        Assert.Equal(EstadoSaga.ExecucaoLiberada, (await SagaAsync(id)).Estado);
        sqs.Verify(s => s.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsumirMensagemComTraceT_ComandoGeradoPeloHandlerEGravadoComTraceparentDoTraceT()
    {
        var (id, _, _) = await CriarOSAsync();
        var itens = new[] { new { insumoId = Guid.NewGuid(), quantidade = 2 } };
        await Atual.Processador.ProcessarAsync(Aprovacao(id, ("itens", itens)), CancellationToken.None);
        Assert.Equal(EstadoSaga.ReservandoInsumos, (await SagaAsync(id)).Estado);
        var reservados = Mensagem(TiposMensagem.InsumosReservados, id, null, null, ("itens", Array.Empty<object>()), ("traceparent", TraceparentT)).Corpo;

        await ConsumirAsync(SqsFalso(), reservados);

        var saga = await SagaAsync(id);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, saga.Estado);
        var liberada = Assert.Single(saga.Comandos, c => c.Tipo == TiposMensagem.OrdemServicoLiberada);
        var consumidor = Assert.Single(Spans(ActivityKind.Consumer, id));
        var traceparent = Json.Ler(liberada.Corpo).GetProperty("traceparent").GetString();
        Assert.Equal($"00-{TraceT}-{consumidor.SpanId.ToHexString()}-01", traceparent);
    }

    [Fact]
    public async Task ConsumirExecucaoConcluidaComTraceT_ProcessaSobOTraceTEAvancaASaga()
    {
        var s = await IniciarSagaAsync();
        var concluida = Mensagem(TiposMensagem.ExecucaoConcluida, s.OrdemServicoId, null, null, ("traceparent", TraceparentT)).Corpo;

        await ConsumirAsync(SqsFalso(), concluida);

        Assert.Equal(EstadoSaga.ExecucaoConcluida, (await SagaAsync(s.OrdemServicoId)).Estado);
        var span = Assert.Single(Spans(ActivityKind.Consumer, s.OrdemServicoId));
        Assert.Equal("processar execucao.concluida.v1", span.OperationName);
        Assert.Equal(TraceT, span.TraceId.ToHexString());
        Assert.Empty(Atual.Envio.Dlq);
    }

    [Theory]
    [InlineData("sem-traceparent")]
    [InlineData("traceparent-invalido")]
    [InlineData("campo-desconhecido")]
    public async Task ConsumirMensagemSemContextoValido_ProcessaComoHojeEmSpanRaizNovoSemDlq(string caso)
    {
        var (id, _, _) = await CriarOSAsync();
        var corpo = caso switch
        {
            "traceparent-invalido" => Aprovacao(id, ("traceparent", "abc")),
            "campo-desconhecido" => Aprovacao(id, ("campoQueNinguemConhece", new { a = 1 })),
            _ => Aprovacao(id)
        };
        Assert.True(MensagemSaga.TentarLer(corpo, out var lida, out _));
        Assert.Equal(id, lida!.OrdemServicoId);
        var sqs = SqsFalso();

        // Uma Activity ambiente (do laço de leitura, por exemplo) não pode virar pai do processamento.
        ActivityTraceId ambiente;
        using (var atividade = FonteTeste.StartActivity("laco de leitura"))
        {
            ambiente = atividade!.TraceId;
            await ConsumirAsync(sqs, corpo);
        }

        var saga = await SagaAsync(id);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, saga.Estado);
        Assert.Equal([TiposMensagem.OrdemServicoLiberada], Atual.Envio.TiposEnviados);
        Assert.Empty(Atual.Envio.Dlq);
        Assert.Empty(Atual.Alertas.Alertas);
        sqs.Verify(s => s.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        var span = Assert.Single(Spans(ActivityKind.Consumer, id));
        Assert.Equal("processar orcamento.aprovado.v1", span.OperationName);
        Assert.Null(span.Parent);
        Assert.Null(span.ParentId);
        Assert.Equal(default, span.ParentSpanId);
        Assert.NotEqual(ambiente, span.TraceId);
        Assert.NotEqual(TraceT, span.TraceId.ToHexString());
    }
}
