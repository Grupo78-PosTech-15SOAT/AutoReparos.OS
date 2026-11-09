using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class AuthorizationContracts
{
    [Theory]
    [InlineData(false, 401)]
    [InlineData(true, 403)]
    public async Task UnauthorizedOrderAccess(bool authenticated, int expected)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddAuthentication(o => { o.DefaultAuthenticateScheme = "Test"; o.DefaultChallengeScheme = "Test"; o.DefaultForbidScheme = "Test"; })
                .AddScheme<AuthenticationSchemeOptions, UnauthorizedAuth>("Test", _ => { })));
        using var client = factory.CreateClient();
        if (authenticated) client.DefaultRequestHeaders.Add("X-Authenticated", "yes");
        var id = Guid.NewGuid();
        var get = await client.GetAsync($"/api/v1/ordens-servico/{id}");
        var patch = await client.PatchAsJsonAsync($"/api/v1/ordens-servico/{id}", new { observacao = "teste" });
        var post = await client.PostAsJsonAsync("/api/v1/ordens-servico", new { clienteId = Guid.NewGuid(), veiculoId = Guid.NewGuid() });
        Assert.Equal((HttpStatusCode)expected, get.StatusCode);
        Assert.Equal((HttpStatusCode)expected, patch.StatusCode);
        Assert.Equal((HttpStatusCode)expected, post.StatusCode);
        Assert.DoesNotContain(id.ToString(), await get.Content.ReadAsStringAsync());
        Assert.DoesNotContain(id.ToString(), await patch.Content.ReadAsStringAsync());
    }
}
public sealed class UnauthorizedAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("X-Authenticated")) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "cliente"), new Claim(ClaimTypes.Role, "Cliente")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
