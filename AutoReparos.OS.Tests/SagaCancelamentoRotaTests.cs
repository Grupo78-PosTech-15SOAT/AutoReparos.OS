using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AutoReparos.OS.Infrastructure;

/// <summary>
/// Rota <c>POST /api/v1/ordens-servico/{id}/cancelamento</c>, o gatilho de produção da compensação (C4): API real,
/// PostgreSQL e DynamoDB Local reais, fila e alertas falsos.
/// </summary>
public sealed class SagaCancelamentoRotaTests(SagaAmbiente ambiente) : SagaRotaBase(ambiente)
{
    private static string Rota(Guid id) => $"/api/v1/ordens-servico/{id}/cancelamento";

    private static async Task<JsonElement> CorpoAsync(HttpResponseMessage resposta) =>
        JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task AssertAceitoAsync(HttpResponseMessage resposta, Guid ordemServicoId, Guid? correlacaoId, string estado)
    {
        Assert.Equal(HttpStatusCode.Accepted, resposta.StatusCode);
        var corpo = await CorpoAsync(resposta);
        Assert.Equal(["correlacaoId", "estado", "ordemServicoId"], corpo.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(ordemServicoId, corpo.GetProperty("ordemServicoId").GetGuid());
        if (correlacaoId is null) Assert.Equal(JsonValueKind.Null, corpo.GetProperty("correlacaoId").ValueKind);
        else Assert.Equal(correlacaoId, corpo.GetProperty("correlacaoId").GetGuid());
        Assert.Equal(estado, corpo.GetProperty("estado").GetString());
    }

    private static async Task AssertInvalidoAsync(HttpResponseMessage resposta)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Equal("ordem_servico_invalida", (await CorpoAsync(resposta)).GetProperty("codigo").GetString());
    }

    private async Task<int> EventosDeOutboxAsync(Guid id)
    {
        using var scope = Fabrica.Services.CreateScope();
        var contexto = scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>();
        return await contexto.MensagensPendentes.CountAsync(m => m.Conteudo.Contains(id.ToString()));
    }

    // ---- Autorização, 404 e 422 ----

    [Theory]
    [InlineData(null, 401)]
    [InlineData("Cliente", 403)]
    [InlineData("Mecanico", 403)]
    [InlineData("ServicoOS", 403)]
    [InlineData("Administrador", 202)]
    [InlineData("Atendente", 202)]
    public async Task CompensatesEachFailedStage_CancellationRouteIsRestrictedToAdministratorAndAttendant(string? papel, int esperado)
    {
        var id = await CriarOSPelaApiAsync();
        using var cliente = Cliente(papel);

        var resposta = await Cancelar(cliente, id, new { motivo = "cliente desistiu" });

        Assert.Equal((HttpStatusCode)esperado, resposta.StatusCode);
        if (esperado == 202)
        {
            Assert.Equal("Cancelada", await StatusOSAsync(id));
            return;
        }
        Assert.Equal("Recebida", await StatusOSAsync(id));
        Assert.Equal(["Recebida"], await HistoricoOSAsync(id));
        Assert.Null(await Sagas.ObterAsync(id, CancellationToken.None));
        var texto = await resposta.Content.ReadAsStringAsync();
        Assert.DoesNotContain(id.ToString(), texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cliente desistiu", texto, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompensatesEachFailedStage_CancellationRouteReturns404ForAMissingOrder()
    {
        using var cliente = Cliente();

        var resposta = await Cancelar(cliente, Guid.NewGuid(), new { motivo = "cliente desistiu" });

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Empty(Envio.Enviados);
    }

    [Fact]
    public async Task CompensatesEachFailedStage_CancellationRouteReturns422ForInvalidReasonOrBodyWithoutAnyEffect()
    {
        var id = await CriarOSPelaApiAsync();
        var aprovada = await CriarOSPelaApiAsync();
        await AprovarOrcamentoAsync(aprovada);
        var enviosAntes = Envio.Enviados.Count;
        var outboxAntes = await EventosDeOutboxAsync(id);
        using var cliente = Cliente();
        var corpos = new (string? Corpo, string ContentType)[]
        {
            ("""{"motivo":""}""", "application/json"),
            ("""{"motivo":"   "}""", "application/json"),
            ("""{"motivo":null}""", "application/json"),
            ("""{"motivo":5}""", "application/json"),
            ("""{"outro":"x"}""", "application/json"),
            ($$"""{"motivo":"{{new string('x', 2001)}}"}""", "application/json"),
            ("""{"motivo":"a\u0000b"}""", "application/json"),
            ("""{"motivo":"\ud800"}""", "application/json"),
            ("{quebrado", "application/json"),
            ("", "application/json"),
            ("[]", "application/json"),
            (null, "application/json"),
            ("""{"motivo":"cliente desistiu"}""", "text/plain"),
        };

        foreach (var (corpo, contentType) in corpos)
        {
            await AssertInvalidoAsync(await Enviar(cliente, HttpMethod.Post, Rota(id), corpo, contentType));
            await AssertInvalidoAsync(await Enviar(cliente, HttpMethod.Post, Rota(aprovada), corpo, contentType));
        }

        Assert.Equal("Recebida", await StatusOSAsync(id));
        Assert.Equal(outboxAntes, await EventosDeOutboxAsync(id));
        Assert.Null(await Sagas.ObterAsync(id, CancellationToken.None));
        Assert.Equal(EstadoSaga.ExecucaoLiberada, (await SagaAsync(aprovada)).Estado);
        Assert.Equal(enviosAntes, Envio.Enviados.Count);
        Assert.Empty(Alertas.Alertas);

        // O limite é inclusivo: exatamente 2000 caracteres é aceito.
        var limite = await Cancelar(cliente, id, new { motivo = new string('x', 2000) });
        await AssertAceitoAsync(limite, id, null, "Cancelada");
    }

    // ---- OS sem Saga ----

    [Fact]
    public async Task CompensatesEachFailedStage_CancellationRouteCancelsAnOrderWithoutASagaDirectlyAndIdempotently()
    {
        var id = await CriarOSPelaApiAsync();
        using var cliente = Cliente("Administrador");

        var primeira = await Cancelar(cliente, id, new { motivo = "cliente desistiu" });

        await AssertAceitoAsync(primeira, id, null, "Cancelada");
        Assert.Equal("Cancelada", await StatusOSAsync(id));
        Assert.Equal(["Cancelada", "Recebida"], (await HistoricoOSAsync(id)).Order(StringComparer.Ordinal));
        Assert.Equal(2, await EventosDeOutboxAsync(id)); // criada + atualizada
        Assert.Null(await Sagas.ObterAsync(id, CancellationToken.None));
        Assert.Empty(Envio.Tentativas);

        var repetida = await Cancelar(cliente, id, new { motivo = "outra vez" });

        await AssertAceitoAsync(repetida, id, null, "Cancelada");
        Assert.Equal(2, (await HistoricoOSAsync(id)).Length);
        Assert.Equal(2, await EventosDeOutboxAsync(id));
        Assert.Empty(Envio.Tentativas);
        Assert.Empty(Alertas.Alertas);
    }

    // ---- Fluxo completo com Saga ----

    [Fact]
    public async Task CompensatesEachFailedStage_CancellationRouteWithOnlyStageOneDoneCompensatesTheBudgetAndCancelsTheOrder()
    {
        var id = await CriarOSPelaApiAsync();
        Controle.FalhasRestantes = 100; // a sincronização da OS falha: a Saga fica em OrcamentoAprovado, sem liberar a execução
        var orcamentoId = await AprovarOrcamentoAsync(id);
        var inicial = await SagaAsync(id);
        Assert.Equal(EstadoSaga.OrcamentoAprovado, inicial.Estado);
        Assert.Empty(Envio.Tentativas);
        using var cliente = Cliente();

        var resposta = await Cancelar(cliente, id, new { motivo = "cliente desistiu" });

        await AssertAceitoAsync(resposta, id, inicial.CorrelacaoId, "Compensando");
        Assert.Equal(["billing.orcamento.cancelar.v1"], Envio.TiposEnviados);
        var comando = Json.Ler(Envio.Enviados[0].Corpo);
        Assert.Equal(DestinoComando.Billing, Envio.Enviados[0].Destino);
        Assert.Equal(orcamentoId, comando.GetProperty("orcamentoId").GetGuid());
        Assert.Equal(inicial.CorrelacaoId, comando.GetProperty("correlacaoId").GetGuid());
        Assert.Equal("cliente desistiu", comando.GetProperty("motivo").GetString());

        await ReceberAsync(TiposMensagem.OrcamentoCancelado, id, ("orcamentoId", orcamentoId));
        Assert.Equal(EstadoSaga.Compensada, (await SagaAsync(id)).Estado);

        Controle.FalhasRestantes = 0;
        Relogio.Avancar(TimeSpan.FromHours(1));
        await Despachador.ExecutarCicloAsync(CancellationToken.None);

        Assert.Equal("Cancelada", await StatusOSAsync(id));
        Assert.Equal(["Cancelada", "OrcamentoAprovado", "Recebida"], (await HistoricoOSAsync(id)).Order(StringComparer.Ordinal));
        Assert.Equal(["billing.orcamento.cancelar.v1"], Envio.TiposEnviados);
        // Repetir depois de compensada é idempotente: 202 com o estado final, sem novo comando.
        await AssertAceitoAsync(await Cancelar(cliente, id, new { motivo = "de novo" }), id, inicial.CorrelacaoId, "Compensada");
        Assert.Equal(["billing.orcamento.cancelar.v1"], Envio.TiposEnviados);
        // Só os alertas da sincronização da OS que falhou de propósito; nenhum alerta da compensação.
        Assert.NotEmpty(Alertas.Alertas);
        Assert.All(Alertas.Codigos, codigo => Assert.Equal("saga.sincronizacao-os-falhou", codigo));
    }

    [Fact]
    public async Task CompensatesEachFailedStage_CancellationRouteWithStageTwoDoneCompensatesExecutionThenBillingInReverseOrder()
    {
        var id = await CriarOSPelaApiAsync();
        var orcamentoId = await AprovarOrcamentoAsync(id);
        var saga = await SagaAsync(id);
        Assert.Equal(EstadoSaga.ExecucaoLiberada, saga.Estado);
        Assert.Equal(["ordem-servico.liberada.v1"], Envio.TiposEnviados);
        using var cliente = Cliente("Administrador");

        var resposta = await Cancelar(cliente, id, new { motivo = "cliente desistiu" });

        await AssertAceitoAsync(resposta, id, saga.CorrelacaoId, "Compensando");
        Assert.Equal(["ordem-servico.liberada.v1", "execucao.cancelar.v1"], Envio.TiposEnviados);
        Assert.Equal(DestinoComando.Execucao, Envio.Enviados[1].Destino);
        Assert.Equal("cliente desistiu", Json.Ler(Envio.Enviados[1].Corpo).GetProperty("motivo").GetString());

        // Repetir enquanto compensa devolve 202 com o mesmo estado e não gera novos comandos.
        await AssertAceitoAsync(await Cancelar(cliente, id, new { motivo = "cliente desistiu" }), id, saga.CorrelacaoId, "Compensando");
        Assert.Equal(2, Envio.Enviados.Count);

        await ReceberAsync(TiposMensagem.ExecucaoCancelada, id, ("execucaoId", Guid.NewGuid()));
        Assert.Equal(["ordem-servico.liberada.v1", "execucao.cancelar.v1", "billing.orcamento.cancelar.v1"], Envio.TiposEnviados);
        Assert.Equal(DestinoComando.Billing, Envio.Enviados[2].Destino);
        Assert.Equal(orcamentoId, Json.Ler(Envio.Enviados[2].Corpo).GetProperty("orcamentoId").GetGuid());

        await ReceberAsync(TiposMensagem.OrcamentoCancelado, id, ("orcamentoId", orcamentoId));

        var final = await SagaAsync(id);
        Assert.Equal(EstadoSaga.Compensada, final.Estado);
        Assert.Equal("Cancelada", await StatusOSAsync(id));
        Assert.Equal(["Cancelada", "EmExecucao", "OrcamentoAprovado", "Recebida"], (await HistoricoOSAsync(id)).Order(StringComparer.Ordinal));
        await AssertAceitoAsync(await Cancelar(cliente, id, new { motivo = "de novo" }), id, saga.CorrelacaoId, "Compensada");
        Assert.Equal(3, Envio.Enviados.Count);
        Assert.Empty(Alertas.Alertas);
    }

    [Fact]
    public async Task CompensatesEachFailedStage_CancellationRouteDiscardsAReleaseThatWasNeverSent()
    {
        Envio.Falha = (_, corpo) => Json.Tipo(corpo) == "ordem-servico.liberada.v1" ? new IOException("SQS fora do ar") : null;
        var id = await CriarOSPelaApiAsync();
        var orcamentoId = await AprovarOrcamentoAsync(id);
        var saga = await SagaAsync(id);
        Assert.True(Assert.Single(saga.Comandos).Pendente);
        using var cliente = Cliente();

        var resposta = await Cancelar(cliente, id, new { motivo = "cliente desistiu" });

        await AssertAceitoAsync(resposta, id, saga.CorrelacaoId, "Compensando");
        Assert.Equal(["billing.orcamento.cancelar.v1"], Envio.TiposEnviados);
        var comandos = (await SagaAsync(id)).Comandos;
        Assert.False(comandos[0].Pendente);
        Assert.Equal(Relogio.Agora, comandos[0].DescartadoEm);
        Assert.Null(comandos[0].EnviadoEm);

        await ReceberAsync(TiposMensagem.OrcamentoCancelado, id, ("orcamentoId", orcamentoId));
        Relogio.Avancar(TimeSpan.FromMinutes(10));
        Envio.Falha = (_, _) => null;
        Assert.Equal(0, await Despachador.ExecutarCicloAsync(CancellationToken.None));

        Assert.Equal(EstadoSaga.Compensada, (await SagaAsync(id)).Estado);
        Assert.Equal(["billing.orcamento.cancelar.v1"], Envio.TiposEnviados);
        Assert.Equal("Cancelada", await StatusOSAsync(id));
    }

    [Fact]
    public async Task CompensatesEachFailedStage_CancellationRouteReturns409AfterExecutionConcludedOrPaymentCapturedAndNeverRefunds()
    {
        var id = await CriarOSPelaApiAsync();
        await AprovarOrcamentoAsync(id);
        await ReceberAsync(TiposMensagem.ExecucaoConcluida, id, ("execucaoId", Guid.NewGuid()));
        var enviosAntes = Envio.Enviados.Count;
        using var cliente = Cliente();

        var aposExecucao = await Cancelar(cliente, id, new { motivo = "tarde demais" });

        Assert.Equal(HttpStatusCode.Conflict, aposExecucao.StatusCode);
        Assert.Equal(["codigo"], (await CorpoAsync(aposExecucao)).EnumerateObject().Select(p => p.Name));
        Assert.Equal("cancelamento_indisponivel", (await CorpoAsync(aposExecucao)).GetProperty("codigo").GetString());
        Assert.Equal(EstadoSaga.ExecucaoConcluida, (await SagaAsync(id)).Estado);
        Assert.Equal("Concluida", await StatusOSAsync(id));
        var alerta = Assert.Single(Alertas.Alertas);
        Assert.Equal("saga.cancelamento-recusado", alerta.Codigo);

        // Repetir continua sendo 409 e não registra um novo alerta.
        Assert.Equal(HttpStatusCode.Conflict, (await Cancelar(cliente, id, new { motivo = "tarde demais" })).StatusCode);
        Assert.Single(Alertas.Alertas);

        await ReceberAsync(TiposMensagem.PagamentoResultado, id, ("pagamentoId", Guid.NewGuid()), ("status", "Capturado"), ("referencia", "mp-1"));
        Assert.Equal(EstadoSaga.PagamentoCapturado, (await SagaAsync(id)).Estado);

        var aposPagamento = await Cancelar(cliente, id, new { motivo = "tarde demais" });

        Assert.Equal(HttpStatusCode.Conflict, aposPagamento.StatusCode);
        Assert.Equal("cancelamento_indisponivel", (await CorpoAsync(aposPagamento)).GetProperty("codigo").GetString());
        Assert.Equal("Paga", await StatusOSAsync(id));
        Assert.Equal(enviosAntes, Envio.Enviados.Count);
        Assert.DoesNotContain(Envio.TiposEnviados, t => t.Contains("cancelar"));
    }
}
