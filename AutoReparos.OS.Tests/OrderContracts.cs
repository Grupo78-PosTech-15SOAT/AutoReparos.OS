using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

public sealed class OrderContracts : IAsyncLifetime
{
    private readonly PostgreSqlContainer db = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly ConcurrencyGate gate = new();
    private WebApplicationFactory<Program> factory = null!;
    public async Task InitializeAsync()
    {
        await db.StartAsync();
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OrdemServicoDbContext>>();
            services.RemoveAll<OrdemServicoDbContext>();
            services.AddDbContext<OrdemServicoDbContext>(o => o.UseNpgsql(db.GetConnectionString()));
            services.AddSingleton<ICadastros, CadastrosAceitaTudo>();
            services.AddScoped<IOrdemServicoRepository>(sp => new GatedRepository(sp.GetRequiredService<OrdemServicoDbContext>(), gate));
            services.AddAuthentication(o => { o.DefaultAuthenticateScheme = "Test"; o.DefaultChallengeScheme = "Test"; }).AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
        }));
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>().Database.MigrateAsync();
    }
    public async Task DisposeAsync() { await factory.DisposeAsync(); await db.DisposeAsync(); }
    private HttpClient Authorized(string role = "Atendente") { var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Test-Role", role); return client; }

    public static TheoryData<string> AllowedRoles => ["Administrador", "Atendente", "Mecanico", "ServicoOS"];

    private static Task<HttpResponseMessage> Raw(HttpClient client, HttpMethod method, string url, string? body, string contentType = "application/json")
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }
        return client.SendAsync(request);
    }

    private static async Task<Guid> CreateOrder(HttpClient client, string? observacao = "original")
    {
        var response = await client.PostAsJsonAsync("/api/v1/ordens-servico", new { clienteId = Guid.NewGuid(), veiculoId = Guid.NewGuid(), observacao });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Created>())!.Id;
    }

    private async Task<State> StateOf(Guid? id = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>();
        var ordem = id is null ? null : await context.OrdensServico.AsNoTracking().Include(x => x.Historico).SingleOrDefaultAsync(x => x.Id == id);
        return new State(ordem?.Observacao, ordem?.Historico.Count ?? 0, await context.OrdensServico.CountAsync(), await context.MensagensPendentes.CountAsync());
    }

    private async Task<List<JsonElement>> Outbox()
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>();
        var mensagens = await context.MensagensPendentes.AsNoTracking().OrderBy(x => x.CriadoEm).ToListAsync();
        return mensagens.Select(m =>
        {
            var evento = JsonDocument.Parse(m.Conteudo).RootElement.Clone();
            Assert.Equal(m.Id, evento.GetProperty("eventId").GetGuid());
            Assert.Equal(new[] { "clienteId", "eventId", "id", "observacao", "status", "tipo", "veiculoId" }, evento.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(evento.EnumerateObject(), p => char.IsUpper(p.Name[0]));
            return evento;
        }).ToList();
    }

    private static async Task AssertInvalid(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ordem_servico_invalida", body.RootElement.GetProperty("codigo").GetString());
    }

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
        Assert.Equal(new State("ruído", 1, 1, 1), await StateOf(created.Id));
        var evento = Assert.Single(await Outbox());
        Assert.Equal("ordem-servico.criada.v1", evento.GetProperty("tipo").GetString());
        Assert.Equal(created.Id, evento.GetProperty("id").GetGuid());
    }

    [Theory]
    [MemberData(nameof(AllowedRoles))]
    public async Task CreateValidOrder_AcceptsAllowedRoles(string role)
    {
        using var client = Authorized(role);
        var id = await CreateOrder(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/ordens-servico/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/v1/ordens-servico/{id}", new { observacao = "novo" })).StatusCode);
    }

    [Fact]
    public async Task CreateValidOrder_AcceptsObservationOfExactly2000Characters()
    {
        using var client = Authorized();
        var id = await CreateOrder(client, new string('x', 2000));
        Assert.Equal(2000, (await StateOf(id)).Observacao!.Length);
    }

    [Fact]
    public async Task InvalidOrderReturns422()
    {
        using var client = Authorized();
        var id = await CreateOrder(client);
        var before = await StateOf(id);
        var response = await client.PatchAsJsonAsync($"/api/v1/ordens-servico/{id}", new { observacao = new string('x', 2001) });
        await AssertInvalid(response);
        Assert.Equal(before, await StateOf(id));
        Assert.Equal("original", before.Observacao);
        Assert.Single(await Outbox());
    }

    [Fact]
    public async Task InvalidOrderReturns422_BoundaryOf2000And2001CharactersOnCreateAndUpdate()
    {
        using var client = Authorized();
        var max = new string('x', 2000);
        var over = new string('x', 2001);
        var id = await CreateOrder(client);
        var accepted = await client.PatchAsJsonAsync($"/api/v1/ordens-servico/{id}", new { observacao = max });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var before = await StateOf(id);
        Assert.Equal(max, before.Observacao);
        var outboxBefore = (await Outbox()).Count;
        await AssertInvalid(await client.PatchAsJsonAsync($"/api/v1/ordens-servico/{id}", new { observacao = over }));
        Assert.Equal(before, await StateOf(id));
        await AssertInvalid(await client.PostAsJsonAsync("/api/v1/ordens-servico", new { clienteId = Guid.NewGuid(), veiculoId = Guid.NewGuid(), observacao = over }));
        Assert.Equal(before, await StateOf(id));
        Assert.Equal(outboxBefore, (await Outbox()).Count);
    }

    [Fact]
    public async Task InvalidOrderReturns422_ForInvalidCreationWithoutPersisting()
    {
        using var client = Authorized();
        var valid = Guid.NewGuid().ToString();
        var bodies = new (string? Body, string ContentType)[]
        {
            ($$"""{"clienteId":"{{Guid.Empty}}","veiculoId":"{{valid}}"}""", "application/json"),
            ($$"""{"clienteId":"{{valid}}","veiculoId":"{{Guid.Empty}}"}""", "application/json"),
            ($$"""{"clienteId":"{{valid}}"}""", "application/json"),
            ($$"""{"veiculoId":"{{valid}}"}""", "application/json"),
            ($$"""{"clienteId":"nao-e-guid","veiculoId":"{{valid}}"}""", "application/json"),
            ($$"""{"clienteId":123,"veiculoId":"{{valid}}"}""", "application/json"),
            ($$"""{"clienteId":"{{valid}}","veiculoId":"{{valid}}","observacao":5}""", "application/json"),
            ($$"""{"clienteId":"{{valid}}","veiculoId":"{{valid}}","observacao":"{{new string('x', 2001)}}"}""", "application/json"),
            ("{quebrado", "application/json"),
            ("", "application/json"),
            ("[]", "application/json"),
            (null, "application/json"),
            ($$"""{"clienteId":"{{valid}}","veiculoId":"{{valid}}"}""", "text/plain"),
            ($$"""{"clienteId":"{{valid}}","veiculoId":"{{valid}}","observacao":"a\u0000b"}""", "application/json"),
            ($$"""{"clienteId":"{{valid}}","veiculoId":"{{valid}}","observacao":"\ud800"}""", "application/json"),
            ($$"""{"clienteId":"\ud800","veiculoId":"{{valid}}"}""", "application/json"),
        };
        foreach (var (body, contentType) in bodies)
        {
            await AssertInvalid(await Raw(client, HttpMethod.Post, "/api/v1/ordens-servico", body, contentType));
        }
        Assert.Equal(new State(null, 0, 0, 0), await StateOf());
    }

    [Fact]
    public async Task InvalidOrderReturns422_ForMalformedUpdateWithoutChangingState()
    {
        using var client = Authorized();
        var id = await CreateOrder(client);
        var before = await StateOf(id);
        var bodies = new (string? Body, string ContentType)[]
        {
            ("{quebrado", "application/json"),
            ("", "application/json"),
            (null, "application/json"),
            ("[]", "application/json"),
            ("\"texto\"", "application/json"),
            ("""{"observacao":5}""", "application/json"),
            ("""{"observacao":"novo"}""", "text/plain"),
            ("""{"observacao":"a\u0000b"}""", "application/json"),
            ("""{"observacao":"\ud800"}""", "application/json"),
        };
        foreach (var (body, contentType) in bodies)
        {
            await AssertInvalid(await Raw(client, HttpMethod.Patch, $"/api/v1/ordens-servico/{id}", body, contentType));
            Assert.Equal(before, await StateOf(id));
        }
        Assert.Single(await Outbox());
    }

    [Fact]
    public async Task InvalidOrderReturns422_UpdateWithoutObservationKeepsStateAndNullClearsIt()
    {
        using var client = Authorized();
        var id = await CreateOrder(client);
        var before = await StateOf(id);
        var empty = await Raw(client, HttpMethod.Patch, $"/api/v1/ordens-servico/{id}", "{}");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal(before, await StateOf(id));
        Assert.Single(await Outbox());
        var cleared = await Raw(client, HttpMethod.Patch, $"/api/v1/ordens-servico/{id}", """{"observacao":null}""");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await StateOf(id)).Observacao);
        Assert.Equal(2, (await Outbox()).Count);
    }

    [Fact]
    public async Task InvalidOrderReturns422_UpdateSuccessAndMissingOrder()
    {
        using var client = Authorized();
        var missing = await client.PatchAsJsonAsync($"/api/v1/ordens-servico/{Guid.NewGuid()}", new { observacao = "ok" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(new State(null, 0, 0, 0), await StateOf());
        var id = await CreateOrder(client, "antes");
        var updated = await client.PatchAsJsonAsync($"/api/v1/ordens-servico/{id}", new { observacao = "depois" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("depois", (await StateOf(id)).Observacao);
        var outbox = await Outbox();
        Assert.Equal(2, outbox.Count);
        var evento = outbox[1];
        Assert.Equal("ordem-servico.atualizada.v1", evento.GetProperty("tipo").GetString());
        Assert.Equal(id, evento.GetProperty("id").GetGuid());
        Assert.Equal("Recebida", evento.GetProperty("status").GetString());
        Assert.Equal("depois", evento.GetProperty("observacao").GetString());
        Assert.NotEqual(Guid.Empty, evento.GetProperty("clienteId").GetGuid());
        Assert.NotEqual(Guid.Empty, evento.GetProperty("veiculoId").GetGuid());
        Assert.NotEqual(outbox[0].GetProperty("eventId").GetGuid(), evento.GetProperty("eventId").GetGuid());
    }

    [Fact]
    public async Task InvalidOrderReturns422_ConcurrentUpdatesYieldConflictAndConsistentState()
    {
        using var client = Authorized();
        var id = await CreateOrder(client);
        gate.Barrier = new Barrier(2);
        try
        {
            var requests = new[] { "A", "B" }.Select(valor => Task.Run(async () =>
            {
                var response = await client.PatchAsJsonAsync($"/api/v1/ordens-servico/{id}", new { observacao = valor });
                return (Valor: valor, response.StatusCode, Body: await response.Content.ReadAsStringAsync());
            })).ToArray();
            var results = await Task.WhenAll(requests);
            var winner = Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
            var loser = Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
            using var conflict = JsonDocument.Parse(loser.Body);
            Assert.Equal("ordem_servico_conflito", conflict.RootElement.GetProperty("codigo").GetString());
            Assert.Equal(winner.Valor, (await StateOf(id)).Observacao);
            var outbox = await Outbox();
            Assert.Equal(2, outbox.Count);
            Assert.Equal(winner.Valor, outbox[1].GetProperty("observacao").GetString());
        }
        finally
        {
            gate.Barrier = null;
        }
    }

    [Fact]
    public async Task OrderHistoryIsChronological()
    {
        using var client = Authorized();
        var id = await CreateOrder(client, null);
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>();
            var earlier = DateTimeOffset.UtcNow.AddDays(-1);
            var later = DateTimeOffset.UtcNow.AddDays(1);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO historico_os (\"Id\", \"OrdemServicoId\", \"Status\", \"RegistradoEm\") VALUES ({Guid.NewGuid()}, {id}, {"Finalizada"}, {later}), ({Guid.NewGuid()}, {id}, {"EmDiagnostico"}, {earlier})");
        }
        var response = await client.GetAsync($"/api/v1/ordens-servico/{id}");
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
    private sealed record State(string? Observacao, int Historico, int Orders, int Outbox);
    private sealed record Order(string Status, History[] Historico);
    private sealed record History(string Status, DateTimeOffset RegistradoEm);
}

public sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Role", out var role)) return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test"), new Claim(ClaimTypes.Role, role.ToString())], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}

public sealed class ConcurrencyGate
{
    public volatile Barrier? Barrier;
}

public sealed class GatedRepository(OrdemServicoDbContext inner, ConcurrencyGate gate) : IOrdemServicoRepository
{
    public Task AdicionarAsync(OrdemServico ordem, CancellationToken cancellationToken) => inner.AdicionarAsync(ordem, cancellationToken);
    public Task SalvarAsync(CancellationToken cancellationToken) => inner.SalvarAsync(cancellationToken);
    public Task<PaginaOS> ListarAsync(string? status, int pagina, int tamanho, CancellationToken cancellationToken) => inner.ListarAsync(status, pagina, tamanho, cancellationToken);
    public Task<ResultadoSincronizacao> SincronizarStatusAsync(Guid id, string status, DateTimeOffset agora, CancellationToken cancellationToken) => inner.SincronizarStatusAsync(id, status, agora, cancellationToken);
    public async Task<OrdemServico?> ObterAsync(Guid id, CancellationToken cancellationToken)
    {
        var ordem = await inner.ObterAsync(id, cancellationToken);
        if (ordem is not null && gate.Barrier is { } barrier && !barrier.SignalAndWait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Barreira de concorrência expirou.");
        return ordem;
    }
}
