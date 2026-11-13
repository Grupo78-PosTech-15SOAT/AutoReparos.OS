using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using AutoReparos.OS.Application;
using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;
using AutoReparos.OS.Infrastructure;
using AutoReparos.OS.Infrastructure.Saga;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Base dos testes que passam pela API real (WebApplicationFactory) com PostgreSQL e DynamoDB Local reais: tabelas
/// exclusivas por teste, relógio, aleatoriedade, fila e alertas falsos; o orquestrador, o processador e o despachador
/// são os serviços reais do contêiner de injeção.
/// </summary>
[Collection("saga")]
public abstract class SagaRotaBase(SagaAmbiente ambiente) : IAsyncLifetime
{
    protected readonly Relogio Relogio = new();
    protected readonly ControleSincronizacao Controle = new();
    protected readonly EnvioFalso Envio = new();
    protected readonly AlertadorFalso Alertas = new();
    protected IAmazonDynamoDB Dynamo = null!;
    protected WebApplicationFactory<Program> Fabrica = null!;

    protected ProcessadorMensagemSaga Processador => Fabrica.Services.GetRequiredService<ProcessadorMensagemSaga>();
    protected DespachadorSaga Despachador => Fabrica.Services.GetRequiredService<DespachadorSaga>();
    protected ISagaRepositorio Sagas => Fabrica.Services.GetRequiredService<ISagaRepositorio>();

    public async Task InitializeAsync()
    {
        Dynamo = ambiente.NovoClienteDynamo();
        var sufixo = Guid.NewGuid().ToString("N")[..12];
        var tabelas = new OpcoesDynamoDb { TabelaSaga = $"saga-rota-{sufixo}", TabelaEventos = $"eventos-rota-{sufixo}" };
        await new DynamoDbInicializador(Dynamo, tabelas).GarantirTabelasAsync(CancellationToken.None);
        var opcoes = new OpcoesSaga { Aleatorio = () => 0.5, Aguardar = (_, _) => Task.CompletedTask };
        Fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddDbContext<OrdemServicoDbContext>(o => o.UseNpgsql(ambiente.PostgresConnection));
            services.AddSingleton<ICadastros, CadastrosAceitaTudo>();
            services.AddScoped<IOrdemServicoRepository>(sp => new RepositorioOSComFalha(sp.GetRequiredService<OrdemServicoDbContext>(), Controle));
            services.AddAuthentication(o => { o.DefaultAuthenticateScheme = "Test"; o.DefaultChallengeScheme = "Test"; o.DefaultForbidScheme = "Test"; })
                .AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
            services.AddSingleton(Dynamo);
            services.AddSingleton(tabelas);
            services.AddSingleton<IEnvioComandos>(Envio);
            services.AddSingleton<IAlertador>(Alertas);
            services.AddSingleton<TimeProvider>(Relogio);
            services.AddSingleton(opcoes);
        }));
        using var scope = Fabrica.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await Fabrica.DisposeAsync();
        Dynamo.Dispose();
    }

    protected HttpClient Cliente(string? papel = "Atendente")
    {
        var cliente = Fabrica.CreateClient();
        if (papel is not null) cliente.DefaultRequestHeaders.Add("X-Test-Role", papel);
        return cliente;
    }

    protected static Task<HttpResponseMessage> Enviar(HttpClient cliente, HttpMethod metodo, string url, string? corpo, string contentType = "application/json")
    {
        var requisicao = new HttpRequestMessage(metodo, url);
        if (corpo is not null)
        {
            requisicao.Content = new StringContent(corpo, Encoding.UTF8);
            requisicao.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }
        return cliente.SendAsync(requisicao);
    }

    protected static Task<HttpResponseMessage> Cancelar(HttpClient cliente, Guid id, object corpo) =>
        cliente.PostAsJsonAsync($"/api/v1/ordens-servico/{id}/cancelamento", corpo);

    protected async Task<Guid> CriarOSPelaApiAsync()
    {
        using var cliente = Cliente();
        var resposta = await cliente.PostAsJsonAsync("/api/v1/ordens-servico", new { clienteId = Guid.NewGuid(), veiculoId = Guid.NewGuid(), observacao = "rota de cancelamento" });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    protected static string CorpoMensagem(string tipo, Guid ordemServicoId, Guid? correlacaoId = null, params (string Chave, object? Valor)[] extras)
    {
        var campos = new Dictionary<string, object?> { ["eventId"] = Guid.NewGuid(), ["tipo"] = tipo, ["ordemServicoId"] = ordemServicoId, ["ocorreuEm"] = "2026-11-10T11:59:00Z" };
        if (correlacaoId is not null) campos["correlacaoId"] = correlacaoId;
        foreach (var (chave, valor) in extras) campos[chave] = valor;
        return JsonSerializer.Serialize(campos);
    }

    protected async Task<ResultadoProcessamento> ReceberAsync(string tipo, Guid ordemServicoId, params (string Chave, object? Valor)[] extras) =>
        await Processador.ProcessarAsync(CorpoMensagem(tipo, ordemServicoId, null, extras), CancellationToken.None);

    /// <summary>Entrega <c>orcamento.aprovado.v1</c> da OS criada pela API e devolve o orçamento da Saga.</summary>
    protected async Task<Guid> AprovarOrcamentoAsync(Guid ordemServicoId)
    {
        var orcamentoId = Guid.NewGuid();
        var resultado = await ReceberAsync(TiposMensagem.OrcamentoAprovado, ordemServicoId, ("orcamentoId", orcamentoId), ("clienteId", Guid.NewGuid()));
        Assert.Equal(DesfechoMensagem.Processada, resultado.Desfecho);
        return orcamentoId;
    }

    protected async Task<SagaOS> SagaAsync(Guid ordemServicoId) => (await Sagas.ObterAsync(ordemServicoId, CancellationToken.None))!;

    protected async Task<JsonElement> ObterOSAsync(Guid id)
    {
        using var cliente = Cliente();
        var resposta = await cliente.GetAsync($"/api/v1/ordens-servico/{id}");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    protected async Task<string> StatusOSAsync(Guid id) => (await ObterOSAsync(id)).GetProperty("status").GetString()!;

    protected async Task<string[]> HistoricoOSAsync(Guid id) =>
        (await ObterOSAsync(id)).GetProperty("historico").EnumerateArray().Select(h => h.GetProperty("status").GetString()!).ToArray();
}
