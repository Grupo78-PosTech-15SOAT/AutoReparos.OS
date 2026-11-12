using System.Text.Json;
using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;
using AutoReparos.OS.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Teste de contrato com os payloads de referência do <c>contracts.md</c> (os mesmos literais usados no Billing):
/// o <c>GET</c> real da OS depois de <c>execucao.concluida.v1</c> e o <c>pagamento.resultado.v1</c> do Billing.
/// </summary>
public sealed class SagaContratoEntreServicosTests(SagaAmbiente ambiente) : SagaRotaBase(ambiente)
{
    private const string OrdemServicoReferencia = "11111111-1111-1111-1111-111111111111";
    private const string ClienteReferencia = "22222222-2222-2222-2222-222222222222";
    private const string VeiculoReferencia = "33333333-3333-3333-3333-333333333333";

    /// <summary><c>GET /api/v1/ordens-servico/{id}</c> quando a execução concluiu (o Billing lê só <c>status</c>).</summary>
    private const string GetReferencia =
        """{ "id": "11111111-1111-1111-1111-111111111111", "clienteId": "22222222-2222-2222-2222-222222222222", "veiculoId": "33333333-3333-3333-3333-333333333333", "observacao": null, "status": "Concluida", "historico": [] }""";

    /// <summary><c>pagamento.resultado.v1</c> publicado pelo Billing.</summary>
    private const string PagamentoReferencia =
        """{ "eventId": "44444444-4444-4444-4444-444444444444", "tipo": "pagamento.resultado.v1", "ordemServicoId": "11111111-1111-1111-1111-111111111111", "ocorreuEm": "2026-11-10T12:00:00Z", "pagamentoId": "55555555-5555-5555-5555-555555555555", "status": "Capturado", "referencia": "mp-1" }""";

    private async Task InserirOSDeReferenciaAsync()
    {
        using var scope = Fabrica.Services.CreateScope();
        var contexto = scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>();
        var ordem = Guid.Parse(OrdemServicoReferencia);
        await contexto.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO ordens_servico ("Id", "ClienteId", "VeiculoId", "Observacao", "Status") VALUES ({ordem}, {Guid.Parse(ClienteReferencia)}, {Guid.Parse(VeiculoReferencia)}, NULL, {"Recebida"})""");
        await contexto.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO historico_os ("Id", "OrdemServicoId", "Status", "RegistradoEm") VALUES ({Guid.NewGuid()}, {ordem}, {"Recebida"}, {Relogio.Agora})""");
    }

    [Fact]
    public async Task SagaContract_GetOfTheOrderAfterExecutionConcludedReturnsTheReferencePayloadWithStatusConcluidaAndTheOrderAcceptsTheReferencePayment()
    {
        await InserirOSDeReferenciaAsync();
        var id = Guid.Parse(OrdemServicoReferencia);
        await ReceberAsync(TiposMensagem.OrcamentoAprovado, id, ("orcamentoId", Guid.NewGuid()), ("clienteId", Guid.NewGuid()), ("itens", Array.Empty<object>()));

        var concluida = await ReceberAsync(TiposMensagem.ExecucaoConcluida, id, ("execucaoId", Guid.NewGuid()));

        Assert.Equal(new ResultadoProcessamento(DesfechoMensagem.Processada, ResultadoMensagem.Aplicada, 1), concluida);
        using var cliente = Cliente();
        var resposta = await cliente.GetAsync($"/api/v1/ordens-servico/{id}");
        var texto = await resposta.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Concluida\"", texto);
        Assert.DoesNotContain("ServicoConcluido", texto);
        var real = JsonDocument.Parse(texto).RootElement;
        var referencia = JsonDocument.Parse(GetReferencia).RootElement;
        Assert.Equal(referencia.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal), real.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var campo in new[] { "id", "clienteId", "veiculoId", "observacao", "status" })
            Assert.Equal(referencia.GetProperty(campo).GetRawText(), real.GetProperty(campo).GetRawText());
        // O histórico da referência é vazio só porque o Billing não o lê; o real registra cada transição.
        Assert.Equal(["Concluida", "EmExecucao", "OrcamentoAprovado", "Recebida"], real.GetProperty("historico").EnumerateArray().Select(h => h.GetProperty("status").GetString()!).Order(StringComparer.Ordinal));

        var pagamento = await Processador.ProcessarAsync(PagamentoReferencia, CancellationToken.None);

        Assert.Equal(new ResultadoProcessamento(DesfechoMensagem.Processada, ResultadoMensagem.Aplicada, 1), pagamento);
        var saga = await SagaAsync(id);
        Assert.Equal(EstadoSaga.PagamentoCapturado, saga.Estado);
        Assert.Contains(saga.Registros, r => r.EventId == Guid.Parse("44444444-4444-4444-4444-444444444444")
            && r.Descricao == "Pagamento 55555555-5555-5555-5555-555555555555 Capturado (referência mp-1).");
        Assert.Equal("Paga", await StatusOSAsync(id));
        Assert.Empty(Envio.Dlq);
        Assert.Empty(Alertas.Alertas);
    }
}
