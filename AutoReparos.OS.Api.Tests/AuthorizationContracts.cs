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

public class AuthorizationContracts
{
    [Theory]
    [InlineData(null, 401)]
    [InlineData("Cliente", 403)]
    [InlineData("PapelInexistente", 403)]
    public async Task UnauthorizedOrderAccess(string? role, int expected)
    {
        await using var db = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await db.StartAsync();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OrdemServicoDbContext>>();
            services.RemoveAll<OrdemServicoDbContext>();
            services.AddDbContext<OrdemServicoDbContext>(o => o.UseNpgsql(db.GetConnectionString()));
            services.AddAuthentication(o => { o.DefaultAuthenticateScheme = "Test"; o.DefaultChallengeScheme = "Test"; o.DefaultForbidScheme = "Test"; })
                .AddScheme<AuthenticationSchemeOptions, RoleAuth>("Test", _ => { });
        }));
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>().Database.MigrateAsync();
        }

        var clienteId = Guid.NewGuid();
        var veiculoId = Guid.NewGuid();
        var segredo = $"segredo-{Guid.NewGuid()}";
        using var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Add("X-Role", "Atendente");
        var createdResponse = await owner.PostAsJsonAsync("/api/v1/ordens-servico", new { clienteId, veiculoId, observacao = segredo });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var id = (await createdResponse.Content.ReadFromJsonAsync<Created>())!.Id;

        using var client = factory.CreateClient();
        if (role is not null) client.DefaultRequestHeaders.Add("X-Role", role);
        var get = await client.GetAsync($"/api/v1/ordens-servico/{id}");
        var patch = await client.PatchAsJsonAsync($"/api/v1/ordens-servico/{id}", new { observacao = "alterada" });
        var post = await client.PostAsJsonAsync("/api/v1/ordens-servico", new { clienteId = Guid.NewGuid(), veiculoId = Guid.NewGuid(), observacao = "intrusa" });
        Assert.Equal((HttpStatusCode)expected, get.StatusCode);
        Assert.Equal((HttpStatusCode)expected, patch.StatusCode);
        Assert.Equal((HttpStatusCode)expected, post.StatusCode);
        foreach (var response in new[] { get, patch, post })
        {
            var body = await response.Content.ReadAsStringAsync();
            foreach (var dado in new[] { id.ToString(), clienteId.ToString(), veiculoId.ToString(), segredo, "Recebida", "alterada", "intrusa" })
                Assert.DoesNotContain(dado, body, StringComparison.OrdinalIgnoreCase);
        }

        using var verify = factory.Services.CreateScope();
        var context = verify.ServiceProvider.GetRequiredService<OrdemServicoDbContext>();
        var ordem = Assert.Single(await context.OrdensServico.AsNoTracking().ToListAsync());
        Assert.Equal(id, ordem.Id);
        Assert.Equal(segredo, ordem.Observacao);
        Assert.Equal(1, await context.MensagensPendentes.CountAsync());
    }

    private sealed record Created(Guid Id, string Status);
}

public sealed class RoleAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Role", out var role)) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "teste"), new Claim(ClaimTypes.Role, role.ToString())], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
