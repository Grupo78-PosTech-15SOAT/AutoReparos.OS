using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;

/// <summary>C6, C7 e C11 de saga-compensacoes: cancelamento recusado após a conclusão, travas 409 e console administrativo, pela API real.</summary>
public sealed class SagaConsoleRotaTests(SagaAmbiente ambiente) : SagaRotaBase(ambiente)
{
    private static readonly Guid Oleo = Guid.Parse("1a5f0001-0000-4000-8000-000000000001");

    private sealed record Iniciada(Guid OrdemServicoId, Guid OrcamentoId, Guid CorrelacaoId);

    private async Task<Iniciada> AprovarComInsumosAsync()
    {
        var id = await CriarOSPelaApiAsync();
        var orcamentoId = Guid.NewGuid();
        var correlacao = Guid.NewGuid();
        var corpo = CorpoMensagem(TiposMensagem.OrcamentoAprovado, id, correlacao, ("orcamentoId", orcamentoId), ("clienteId", Guid.NewGuid()), ("itens", new[] { new { insumoId = Oleo, quantidade = 2 } }));
        Assert.Equal(DesfechoMensagem.Processada, (await Processador.ProcessarAsync(corpo, CancellationToken.None)).Desfecho);
        return new Iniciada(id, orcamentoId, correlacao);
    }

    private Task EntregarAsync(string tipo, Iniciada s, params (string Chave, object? Valor)[] extras) =>
        Processador.ProcessarAsync(CorpoMensagem(tipo, s.OrdemServicoId, s.CorrelacaoId, extras), CancellationToken.None);

    private Task ReservadosAsync(Iniciada s) => EntregarAsync(TiposMensagem.InsumosReservados, s, ("itens", Array.Empty<object>()));
    private Task SemSaldoAsync(Iniciada s) => EntregarAsync(TiposMensagem.InsumosReservaFalhou, s, ("motivo", "Saldo insuficiente."), ("itensEmFalta", Array.Empty<object>()));

    private async Task<(HttpStatusCode Status, string? Codigo)> CancelarAsync(Guid id)
    {
        using var cliente = Cliente("Atendente");
        var resposta = await Cancelar(cliente, id, new { motivo = "cliente desistiu" });
        return (resposta.StatusCode, await CodigoAsync(resposta));
    }

    private async Task<(HttpStatusCode Status, string? Codigo)> RetentarAsync(Guid id, string motivo = "saldo reposto")
    {
        using var cliente = Cliente("Administrador");
        var resposta = await cliente.PostAsJsonAsync($"/api/v1/admin/sagas/{id}/retentar", new { motivo });
        return (resposta.StatusCode, await CodigoAsync(resposta));
    }

    private static async Task<string?> CodigoAsync(HttpResponseMessage resposta)
    {
        var texto = await resposta.Content.ReadAsStringAsync();
        return texto.Length > 0 && JsonDocument.Parse(texto).RootElement.TryGetProperty("codigo", out var c) ? c.GetString() : null;
    }

    [Fact]
    public async Task CancelamentoAposConclusaoRecusado()
    {
        var s = await AprovarComInsumosAsync();
        await ReservadosAsync(s);
        await EntregarAsync(TiposMensagem.ExecucaoConcluida, s);

        var (status, codigo) = await CancelarAsync(s.OrdemServicoId);

        Assert.Equal((HttpStatusCode.Conflict, "cancelamento_indisponivel"), (status, codigo));
        var saga = (await Sagas.ObterAsync(s.OrdemServicoId, CancellationToken.None))!;
        Assert.Equal(EstadoSaga.ExecucaoConcluida, saga.Estado);
        // A reserva Confirmada permanece: nenhum comando de compensação foi emitido.
        Assert.Equal(SituacaoEtapa.Concluida, saga.Etapa(5).Situacao);
        Assert.DoesNotContain(Envio.TiposEnviados, t => t is "execucao.cancelar-reserva.v1" or "execucao.cancelar.v1" or "cobranca.cancelar-orcamento.v1");
    }

    [Fact]
    public async Task ComandosConflitantesRecebem409()
    {
        var s = await AprovarComInsumosAsync();

        // ReservandoInsumos: cancelar e retentar esperam.
        Assert.Equal((HttpStatusCode.Conflict, "saga_em_andamento"), await CancelarAsync(s.OrdemServicoId));
        Assert.Equal((HttpStatusCode.Conflict, "saga_em_andamento"), await RetentarAsync(s.OrdemServicoId));
        Assert.DoesNotContain("execucao.cancelar-reserva.v1", Envio.TiposEnviados);

        // Compensando (liberação recusada): um cancelamento novo e a retentativa esperam; a Saga não muda.
        await ReservadosAsync(s);
        await EntregarAsync(TiposMensagem.ExecucaoLiberacaoRecusada, s, ("motivo", "OS inválida"));
        var emCompensacao = (await Sagas.ObterAsync(s.OrdemServicoId, CancellationToken.None))!;
        Assert.Equal(EstadoSaga.Compensando, emCompensacao.Estado);
        var comandos = emCompensacao.Comandos.Count;
        Assert.Equal((HttpStatusCode.Conflict, "saga_em_andamento"), await CancelarAsync(s.OrdemServicoId));
        Assert.Equal((HttpStatusCode.Conflict, "saga_em_andamento"), await RetentarAsync(s.OrdemServicoId));
        Assert.Equal(comandos, (await Sagas.ObterAsync(s.OrdemServicoId, CancellationToken.None))!.Comandos.Count);
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("Atendente", 403)]
    [InlineData("Cliente", 403)]
    [InlineData("Administrador", 200)]
    public async Task ConsoleAdminRegistraAcao_ListaESeuDetalheSoParaAdministrador(string? papel, int esperado)
    {
        var s = await AprovarComInsumosAsync();
        using var cliente = Cliente(papel);

        Assert.Equal(esperado, (int)(await cliente.GetAsync("/api/v1/admin/sagas")).StatusCode);
        Assert.Equal(esperado, (int)(await cliente.GetAsync($"/api/v1/admin/sagas/{s.OrdemServicoId}")).StatusCode);
        var retentar = (int)(await cliente.PostAsJsonAsync($"/api/v1/admin/sagas/{s.OrdemServicoId}/retentar", new { motivo = "x" })).StatusCode;
        if (papel == "Administrador") Assert.Equal(409, retentar);
        else Assert.Equal(papel is null ? 401 : 403, retentar);
    }

    [Fact]
    public async Task ConsoleAdminRegistraAcao()
    {
        var s = await AprovarComInsumosAsync();
        await SemSaldoAsync(s);
        var outra = await AprovarComInsumosAsync();
        using var admin = Cliente("Administrador");

        // Lista com filtro de estado e de inatividade.
        Relogio.Avancar(TimeSpan.FromHours(2));
        var parados = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/sagas?estado=AguardandoInsumos&paradaHaMinutos=60");
        Assert.Equal([s.OrdemServicoId], parados.EnumerateArray().Select(i => i.GetProperty("ordemServicoId").GetGuid()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.GetAsync("/api/v1/admin/sagas?estado=Inexistente")).StatusCode);
        var todas = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/sagas");
        Assert.Contains(outra.OrdemServicoId, todas.EnumerateArray().Select(i => i.GetProperty("ordemServicoId").GetGuid()));

        // Detalhe: estado, etapas, comandos e histórico.
        var detalhe = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/admin/sagas/{s.OrdemServicoId}");
        Assert.Equal("AguardandoInsumos", detalhe.GetProperty("estado").GetString());
        Assert.Equal(s.CorrelacaoId, detalhe.GetProperty("correlacaoId").GetGuid());
        Assert.Contains(detalhe.GetProperty("comandos").EnumerateArray(), c => c.GetProperty("tipo").GetString() == "execucao.reservar-insumos.v1");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/admin/sagas/{Guid.NewGuid()}")).StatusCode);

        // Retentar: 202, comando novo e a ação (autor, motivo e data) no histórico; sem motivo é 422.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync($"/api/v1/admin/sagas/{s.OrdemServicoId}/retentar", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RetentarAsync(Guid.NewGuid())).Status);
        var quando = Relogio.Agora;
        Assert.Equal(HttpStatusCode.Accepted, (await RetentarAsync(s.OrdemServicoId, "saldo reposto pelo fornecedor")).Status);
        var saga = (await Sagas.ObterAsync(s.OrdemServicoId, CancellationToken.None))!;
        Assert.Equal(EstadoSaga.ReservandoInsumos, saga.Estado);
        var registro = Assert.Single(saga.Registros, r => r.Evento == "saga.retentada");
        Assert.Equal(("Retentativa por test: saldo reposto pelo fornecedor.", quando), (registro.Descricao, registro.Em));
        Assert.Equal(3, Envio.TiposEnviados.Count(t => t == "execucao.reservar-insumos.v1"));

        // Fora de AguardandoInsumos a retentativa é recusada com outro código.
        await ReservadosAsync(s);
        Assert.Equal((HttpStatusCode.Conflict, "retentativa_indisponivel"), await RetentarAsync(s.OrdemServicoId));
    }
}
