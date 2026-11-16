using System.Net;
using System.Net.Sockets;
using AutoReparos.OS.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

/// <summary>
/// C2: <c>GET /health</c> é a probe do kubelet e o health check do ALB, que não mandam token. A fábrica liga uma
/// política de fallback que exige usuário autenticado: o /health precisa responder mesmo assim.
/// </summary>
public sealed class SaudeEndpointTests
{
    [Fact]
    public async Task RespondeOkSemTokenComDatabaseAcessivel()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgres.StartAsync();
        await using var fabrica = new Fabrica(postgres.GetConnectionString());
        using var cliente = fabrica.CreateClient();

        var saude = await cliente.GetAsync("/health");
        var protegida = await cliente.GetAsync($"/api/v1/ordens-servico/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, saude.StatusCode);
        Assert.Equal("Healthy", await saude.Content.ReadAsStringAsync());
        // Sem token as rotas de negócio continuam fechadas: o 200 acima não vem de autenticação desligada.
        Assert.Equal(HttpStatusCode.Unauthorized, protegida.StatusCode);
    }

    [Fact]
    public async Task Responde503ComDatabaseFora()
    {
        await using var fabrica = new Fabrica($"Host=127.0.0.1;Port={PortaSemServidor()};Database=os;Username=postgres;Password=postgres;Timeout=5");
        using var cliente = fabrica.CreateClient();

        var saude = await cliente.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, saude.StatusCode);
    }

    private static int PortaSemServidor()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var porta = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return porta;
    }

    private sealed class Fabrica(string conexao) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OrdemServicoDbContext>>();
            services.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<OrdemServicoDbContext>));
            services.AddDbContext<OrdemServicoDbContext>(o => o.UseNpgsql(conexao));
            services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        });
    }
}
