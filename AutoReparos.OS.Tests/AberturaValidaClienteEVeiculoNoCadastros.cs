using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoReparos.OS.Application;
using AutoReparos.OS.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

/// <summary>Cadastros que aceita qualquer cliente e veículo: os testes de contrato da OS não validam o Cadastros.</summary>
public sealed class CadastrosAceitaTudo : ICadastros
{
    public Task<ResultadoValidacaoCadastro> ValidarAsync(Guid clienteId, Guid veiculoId, string? authorization, CancellationToken cancellationToken) =>
        Task.FromResult(ResultadoValidacaoCadastro.Valido);
}

/// <summary>
/// C2: a abertura da OS valida cliente e veículo pelo REST do Cadastros (cliente HTTP real, Cadastros simulado por um
/// manipulador de HTTP), repassa o JWT do usuário ou, sem usuário, a chave interna, e 404 impede a abertura.
/// </summary>
public sealed class AberturaValidaClienteEVeiculoNoCadastros : IAsyncLifetime
{
    private const string ChaveInterna = "chave-interna-do-teste";
    private readonly PostgreSqlContainer db = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly CadastrosSimulado cadastros = new();
    private WebApplicationFactory<Program> factory = null!;

    public async Task InitializeAsync()
    {
        await db.StartAsync();
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cadastros:BaseUrl"] = "http://cadastros.test",
                ["Cadastros:ChaveInterna"] = ChaveInterna
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<OrdemServicoDbContext>>();
                services.RemoveAll<OrdemServicoDbContext>();
                services.AddDbContext<OrdemServicoDbContext>(o => o.UseNpgsql(db.GetConnectionString()));
                services.AddAuthentication(o => { o.DefaultAuthenticateScheme = "Test"; o.DefaultChallengeScheme = "Test"; })
                    .AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
                services.AddHttpClient<ICadastros, CadastrosHttp>().ConfigurePrimaryHttpMessageHandler(() => cadastros);
            });
        });
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync() { await factory.DisposeAsync(); await db.DisposeAsync(); }

    private HttpClient Usuario(string? bearer = "token-do-usuario", string papel = "Atendente")
    {
        var cliente = factory.CreateClient();
        cliente.DefaultRequestHeaders.Add("X-Test-Role", papel);
        if (bearer is not null) cliente.DefaultRequestHeaders.Add("Authorization", $"Bearer {bearer}");
        return cliente;
    }

    private static Task<HttpResponseMessage> Abrir(HttpClient cliente, Guid clienteId, Guid veiculoId) =>
        cliente.PostAsJsonAsync("/api/v1/ordens-servico", new { clienteId, veiculoId, observacao = "teste" });

    private static async Task<string?> CodigoAsync(HttpResponseMessage resposta) =>
        JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement.GetProperty("codigo").GetString();

    [Fact]
    public async Task ClienteEVeiculoExistentes_AbreAAOSRepassandoOJwtDoUsuario()
    {
        var (cliente, veiculo) = cadastros.NovoClienteComVeiculo();

        var resposta = await Abrir(Usuario(), cliente, veiculo);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.Equal(2, cadastros.Chamadas.Count);
        Assert.Contains($"/api/v1/clientes/{cliente}", cadastros.Chamadas.Select(c => c.Caminho));
        Assert.Contains($"/api/v1/veiculos/{veiculo}", cadastros.Chamadas.Select(c => c.Caminho));
        Assert.All(cadastros.Chamadas, c =>
        {
            Assert.Equal("Bearer token-do-usuario", c.Authorization);
            Assert.Null(c.ChaveInterna);
        });
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("Atendente")]
    [InlineData("Mecanico")]
    public async Task PapeisDaOficina_RepassamOJwtDoUsuario(string papel)
    {
        var (cliente, veiculo) = cadastros.NovoClienteComVeiculo();

        var resposta = await Abrir(Usuario(papel: papel), cliente, veiculo);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.All(cadastros.Chamadas, c =>
        {
            Assert.Equal("Bearer token-do-usuario", c.Authorization);
            Assert.Null(c.ChaveInterna);
        });
    }

    [Fact]
    public async Task ContaDeServico_UsaAChaveInternaEmVezDoPropriJwt()
    {
        var (cliente, veiculo) = cadastros.NovoClienteComVeiculo();

        var resposta = await Abrir(Usuario(papel: "ServicoOS"), cliente, veiculo);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.Equal(2, cadastros.Chamadas.Count);
        Assert.All(cadastros.Chamadas, c =>
        {
            Assert.Null(c.Authorization);
            Assert.Equal(ChaveInterna, c.ChaveInterna);
        });
    }

    [Fact]
    public async Task SemUsuario_UsaAChaveInternaDoSegredo()
    {
        var (cliente, veiculo) = cadastros.NovoClienteComVeiculo();
        using var escopo = factory.Services.CreateScope();

        var resultado = await escopo.ServiceProvider.GetRequiredService<ICadastros>().ValidarAsync(cliente, veiculo, null, CancellationToken.None);

        Assert.Equal(ResultadoValidacaoCadastro.Valido, resultado);
        Assert.All(cadastros.Chamadas, c =>
        {
            Assert.Null(c.Authorization);
            Assert.Equal(ChaveInterna, c.ChaveInterna);
        });
    }

    [Fact]
    public async Task ClienteInexistente_Retorna404ENaoAbreAOS()
    {
        var (_, veiculo) = cadastros.NovoClienteComVeiculo();

        var resposta = await Abrir(Usuario(), Guid.NewGuid(), veiculo);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal("cliente_nao_encontrado", await CodigoAsync(resposta));
    }

    [Fact]
    public async Task VeiculoInexistente_Retorna404ENaoAbreAOS()
    {
        var (cliente, _) = cadastros.NovoClienteComVeiculo();

        var resposta = await Abrir(Usuario(), cliente, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal("veiculo_nao_encontrado", await CodigoAsync(resposta));
    }

    [Fact]
    public async Task VeiculoDeOutroCliente_Retorna422ENaoAbreAOS()
    {
        var (cliente, _) = cadastros.NovoClienteComVeiculo();
        var (_, veiculoDeOutro) = cadastros.NovoClienteComVeiculo();

        var resposta = await Abrir(Usuario(), cliente, veiculoDeOutro);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Equal("veiculo_de_outro_cliente", await CodigoAsync(resposta));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task CadastrosRespondeErro_Retorna503ENaoAbreAOS(HttpStatusCode erro)
    {
        var (cliente, veiculo) = cadastros.NovoClienteComVeiculo();
        cadastros.ForcarStatus = erro;

        var resposta = await Abrir(Usuario(), cliente, veiculo);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);
        Assert.Equal("cadastros_indisponivel", await CodigoAsync(resposta));
    }

    [Fact]
    public async Task CorpoInvalido_NaoConsultaOCadastros()
    {
        var resposta = await Usuario().PostAsJsonAsync("/api/v1/ordens-servico", new { clienteId = "invalido", veiculoId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Empty(cadastros.Chamadas);
    }

    /// <summary>Cadastros simulado: registra o que o OS enviou e responde como a API do Cadastros.</summary>
    private sealed class CadastrosSimulado : HttpMessageHandler
    {
        private readonly Dictionary<Guid, Guid> veiculosPorDono = [];
        private readonly HashSet<Guid> clientes = [];
        public List<(string Caminho, string? Authorization, string? ChaveInterna)> Chamadas { get; } = [];
        public HttpStatusCode? ForcarStatus { get; set; }

        public (Guid Cliente, Guid Veiculo) NovoClienteComVeiculo()
        {
            var cliente = Guid.NewGuid();
            var veiculo = Guid.NewGuid();
            clientes.Add(cliente);
            veiculosPorDono[veiculo] = cliente;
            return (cliente, veiculo);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage requisicao, CancellationToken cancellationToken)
        {
            var caminho = requisicao.RequestUri!.AbsolutePath;
            Chamadas.Add((caminho,
                requisicao.Headers.TryGetValues("Authorization", out var a) ? a.Single() : null,
                requisicao.Headers.TryGetValues("X-Chave-Interna", out var k) ? k.Single() : null));

            if (ForcarStatus is { } status) return Task.FromResult(new HttpResponseMessage(status));

            var partes = caminho.Split('/', StringSplitOptions.RemoveEmptyEntries); // api, v1, clientes|veiculos, id
            if (partes.Length == 4 && Guid.TryParse(partes[3], out var id))
            {
                if (partes[2] == "clientes" && clientes.Contains(id))
                    return Task.FromResult(Json(new { id }));
                if (partes[2] == "veiculos" && veiculosPorDono.TryGetValue(id, out var dono))
                    return Task.FromResult(Json(new { id, clienteId = dono }));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(object corpo) => new(HttpStatusCode.OK) { Content = JsonContent.Create(corpo) };
    }
}
