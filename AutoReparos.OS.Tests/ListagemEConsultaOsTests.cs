using System.Net;
using System.Text.Json;
using AutoReparos.OS.Application;
using AutoReparos.OS.Domain;
using AutoReparos.OS.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

/// <summary>C6 e C7 de web-adaptacao: listagem paginada e consulta pública da OS, pela API real com PostgreSQL próprio (banco vazio por classe).</summary>
public sealed class ListagemEConsultaOsTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlContainer db = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private WebApplicationFactory<Program> fabrica = null!;

    public async Task InitializeAsync()
    {
        await db.StartAsync();
        fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OrdemServicoDbContext>>();
            services.RemoveAll<OrdemServicoDbContext>();
            services.AddDbContext<OrdemServicoDbContext>(o => o.UseNpgsql(db.GetConnectionString()));
            services.AddSingleton<ICadastros, CadastrosAceitaTudo>();
            services.AddAuthentication(o => { o.DefaultAuthenticateScheme = "Test"; o.DefaultChallengeScheme = "Test"; o.DefaultForbidScheme = "Test"; })
                .AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
        }));
        using var scope = fabrica.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await fabrica.DisposeAsync();
        await db.DisposeAsync();
    }

    private HttpClient Cliente(string? papel = "Atendente")
    {
        var cliente = fabrica.CreateClient();
        if (papel is not null) cliente.DefaultRequestHeaders.Add("X-Test-Role", papel);
        return cliente;
    }

    /// <summary>Grava <paramref name="quantidade"/> OS, cada uma criada um minuto depois da anterior; as de índice em <paramref name="pagas"/> viram Paga 30 segundos depois.</summary>
    private async Task<List<Guid>> SemearAsync(int quantidade, params int[] pagas)
    {
        using var scope = fabrica.Services.CreateScope();
        var contexto = scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>();
        var ids = new List<Guid>();
        for (var i = 0; i < quantidade; i++)
        {
            var ordem = new OrdemServico(Guid.NewGuid(), Guid.NewGuid(), null, Base.AddMinutes(i));
            if (pagas.Contains(i)) ordem.DefinirStatus(StatusOS.Paga, Base.AddMinutes(i).AddSeconds(30));
            await contexto.AdicionarAsync(ordem, CancellationToken.None);
            ids.Add(ordem.Id);
        }
        await contexto.SalvarAsync(CancellationToken.None);
        return ids;
    }

    private static async Task<JsonElement> CorpoAsync(HttpResponseMessage resposta) => JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<JsonElement> ListarAsync(string consulta)
    {
        using var cliente = Cliente();
        var resposta = await cliente.GetAsync($"/api/v1/ordens-servico{consulta}");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return await CorpoAsync(resposta);
    }

    [Fact]
    public async Task ListaOsPaginadaEFiltrada()
    {
        var ids = await SemearAsync(25, 3, 7, 11);

        var terceira = await ListarAsync("?tamanho=10&pagina=3");
        Assert.Equal(25, terceira.GetProperty("total").GetInt32());
        Assert.Equal(5, terceira.GetProperty("itens").GetArrayLength());

        var padrao = await ListarAsync("");
        Assert.Equal(20, padrao.GetProperty("itens").GetArrayLength());

        var maxima = await ListarAsync("?tamanho=100");
        Assert.Equal(25, maxima.GetProperty("itens").GetArrayLength());

        var pagas = await ListarAsync("?status=Paga");
        Assert.Equal(3, pagas.GetProperty("total").GetInt32());
        Assert.All(pagas.GetProperty("itens").EnumerateArray(), i => Assert.Equal("Paga", i.GetProperty("status").GetString()));
        Assert.Equal(new[] { ids[11], ids[7], ids[3] }, pagas.GetProperty("itens").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));

        var recebidas = await ListarAsync("?status=Recebida&tamanho=100");
        Assert.Equal(22, recebidas.GetProperty("total").GetInt32());

        var item = terceira.GetProperty("itens")[0];
        Assert.All(new[] { "id", "clienteId", "veiculoId", "status", "atualizadaEm" }, nome => Assert.True(item.TryGetProperty(nome, out _), nome));
    }

    [Fact]
    public async Task ListaOsPaginadaEFiltrada_OrdenaPorAtualizadaEmDecrescente()
    {
        var ids = await SemearAsync(5, 0);

        var itens = (await ListarAsync("")).GetProperty("itens").EnumerateArray().ToList();

        // A OS 0 foi paga 30 segundos depois de criada, mas a OS 4 foi criada depois: ordem pelo último registro do histórico.
        Assert.Equal(new[] { ids[4], ids[3], ids[2], ids[1], ids[0] }, itens.Select(i => i.GetProperty("id").GetGuid()));
        var datas = itens.Select(i => i.GetProperty("atualizadaEm").GetDateTimeOffset()).ToList();
        Assert.Equal(datas.OrderByDescending(d => d), datas);
        Assert.Equal(Base.AddMinutes(4), datas[0]);
    }

    [Theory]
    [InlineData("?tamanho=101")]
    [InlineData("?tamanho=0")]
    [InlineData("?tamanho=-1")]
    [InlineData("?tamanho=abc")]
    [InlineData("?pagina=0")]
    [InlineData("?pagina=-2")]
    [InlineData("?pagina=x")]
    [InlineData("?status=Inexistente")]
    [InlineData("?status=paga")]
    [InlineData("?status=4")]
    public async Task ListaOsPaginadaEFiltrada_FiltroInvalidoRecebe422(string consulta)
    {
        using var cliente = Cliente();
        var resposta = await cliente.GetAsync($"/api/v1/ordens-servico{consulta}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Equal("filtro_invalido", (await CorpoAsync(resposta)).GetProperty("codigo").GetString());
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("Cliente", 403)]
    [InlineData("ServicoOS", 403)]
    [InlineData("Administrador", 200)]
    [InlineData("Atendente", 200)]
    [InlineData("Mecanico", 200)]
    public async Task ListaOsPaginadaEFiltrada_ExigePapelDeOficina(string? papel, int esperado)
    {
        using var cliente = Cliente(papel);
        var resposta = await cliente.GetAsync("/api/v1/ordens-servico");
        Assert.Equal(esperado, (int)resposta.StatusCode);
    }

    [Fact]
    public async Task ConsultaPublicaNaoExpoeClienteNemVeiculo()
    {
        var ids = await SemearAsync(1, 0);
        using var anonimo = Cliente(null);

        var resposta = await anonimo.GetAsync($"/api/v1/ordens-servico/{ids[0]}/consulta");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var texto = await resposta.Content.ReadAsStringAsync();
        var corpo = JsonDocument.Parse(texto).RootElement;
        Assert.Equal(ids[0], corpo.GetProperty("id").GetGuid());
        Assert.Equal("Paga", corpo.GetProperty("status").GetString());
        Assert.Equal(new[] { "id", "status", "historico" }, corpo.EnumerateObject().Select(p => p.Name));
        var historico = corpo.GetProperty("historico").EnumerateArray().ToList();
        Assert.Equal(new[] { "Recebida", "Paga" }, historico.Select(h => h.GetProperty("status").GetString()));
        Assert.All(historico, h => Assert.Equal(new[] { "status", "em" }, h.EnumerateObject().Select(p => p.Name)));
        Assert.DoesNotContain("clienteId", texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("veiculoId", texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("observacao", texto, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConsultaPublicaNaoExpoeClienteNemVeiculo_IdDesconhecidoRecebe404()
    {
        using var anonimo = Cliente(null);
        var resposta = await anonimo.GetAsync($"/api/v1/ordens-servico/{Guid.NewGuid()}/consulta");
        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }
}
