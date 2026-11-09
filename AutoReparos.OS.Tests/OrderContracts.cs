using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AutoReparos.OS.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

public sealed class OrderContracts : IAsyncLifetime
{
    private readonly PostgreSqlContainer db = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private WebApplicationFactory<Program> factory = null!;
    public async Task InitializeAsync()
    {
        await db.StartAsync();
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OrdemServicoDbContext>>();
            services.RemoveAll<OrdemServicoDbContext>();
            services.AddDbContext<OrdemServicoDbContext>(o => o.UseNpgsql(db.GetConnectionString()));
            services.AddAuthentication(o => { o.DefaultAuthenticateScheme = "Test"; o.DefaultChallengeScheme = "Test"; }).AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
        }));
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>().Database.MigrateAsync();
    }
    public async Task DisposeAsync() { await factory.DisposeAsync(); await db.DisposeAsync(); }
    private HttpClient Authorized() { var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Test-Auth", "yes"); return client; }

    [Fact]
    public async Task CreateValidOrder()
    {
        using var client = Authorized();
        var response = await client.PostAsJsonAsync("/api/v1/ordens-servico", new { clienteId = Guid.NewGuid(), veiculoId = Guid.NewGuid(), observacao = "ruído" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<Created>();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("Recebida", created.Status);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>();
        Assert.True(await context.OrdensServico.AnyAsync(x => x.Id == created.Id));
        Assert.Equal(1, await context.MensagensPendentes.CountAsync());
    }

    [Fact]
    public async Task InvalidOrderReturns422()
    {
        using var client = Authorized();
        var created = await (await client.PostAsJsonAsync("/api/v1/ordens-servico", new { clienteId = Guid.NewGuid(), veiculoId = Guid.NewGuid(), observacao = "original" })).Content.ReadFromJsonAsync<Created>();
        var response = await client.PatchAsJsonAsync($"/api/v1/ordens-servico/{created!.Id}", new { observacao = new string('x', 2001) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("ordem_servico_invalida", await response.Content.ReadAsStringAsync());
        var body = await client.GetStringAsync($"/api/v1/ordens-servico/{created.Id}");
        Assert.Contains("original", body);
    }

    [Fact]
    public async Task InvalidOrderReturns422_ForInvalidCreationWithoutPersisting()
    {
        using var client = Authorized();
        var response = await client.PostAsJsonAsync("/api/v1/ordens-servico", new { clienteId = Guid.Empty, veiculoId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("ordem_servico_invalida", await response.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>();
        Assert.Empty(await context.OrdensServico.ToListAsync());
        Assert.Empty(await context.MensagensPendentes.ToListAsync());
    }

    [Fact]
    public async Task InvalidOrderReturns422_UpdateSuccessAndMissingOrder()
    {
        using var client = Authorized();
        var missing = await client.PatchAsJsonAsync($"/api/v1/ordens-servico/{Guid.NewGuid()}", new { observacao = "ok" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var created = await (await client.PostAsJsonAsync("/api/v1/ordens-servico", new { clienteId = Guid.NewGuid(), veiculoId = Guid.NewGuid(), observacao = "antes" })).Content.ReadFromJsonAsync<Created>();
        var updated = await client.PatchAsJsonAsync($"/api/v1/ordens-servico/{created!.Id}", new { observacao = "depois" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Contains("depois", await client.GetStringAsync($"/api/v1/ordens-servico/{created.Id}"));
    }

    [Fact]
    public async Task OrderHistoryIsChronological()
    {
        using var client = Authorized();
        var created = await (await client.PostAsJsonAsync("/api/v1/ordens-servico", new { clienteId = Guid.NewGuid(), veiculoId = Guid.NewGuid() })).Content.ReadFromJsonAsync<Created>();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>();
            var earlier = DateTimeOffset.UtcNow.AddDays(-1);
            var later = DateTimeOffset.UtcNow.AddDays(1);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO historico_os (\"Id\", \"OrdemServicoId\", \"Status\", \"RegistradoEm\") VALUES ({Guid.NewGuid()}, {created!.Id}, {"Finalizada"}, {later}), ({Guid.NewGuid()}, {created.Id}, {"EmDiagnostico"}, {earlier})");
        }
        var response = await client.GetAsync($"/api/v1/ordens-servico/{created!.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<Order>();
        Assert.Equal("Recebida", order!.Status);
        Assert.Equal(new[] { "EmDiagnostico", "Recebida", "Finalizada" }, order.Historico.Select(h => h.Status));
        Assert.Equal(order.Historico.OrderBy(h => h.RegistradoEm).Select(h => h.RegistradoEm), order.Historico.Select(h => h.RegistradoEm));
    }

    [Fact]
    public async Task OrderHistoryIsChronological_MissingOrderReturns404()
    {
        using var client = Authorized();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/ordens-servico/{Guid.NewGuid()}")).StatusCode);
    }
    private sealed record Created(Guid Id, string Status);
    private sealed record Order(string Status, History[] Historico);
    private sealed record History(string Status, DateTimeOffset RegistradoEm);
}

public sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("X-Test-Auth")) return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test"), new Claim(ClaimTypes.Role, "Atendente")], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
