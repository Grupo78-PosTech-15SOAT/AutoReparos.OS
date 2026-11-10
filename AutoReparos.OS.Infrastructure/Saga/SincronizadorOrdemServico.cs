using AutoReparos.OS.Application;
using AutoReparos.OS.Application.Saga;
using Microsoft.Extensions.DependencyInjection;

namespace AutoReparos.OS.Infrastructure.Saga;

/// <summary>Abre um escopo de banco por chamada, para que uma falha não contamine as seguintes.</summary>
public sealed class SincronizadorOrdemServico(IServiceScopeFactory scopes) : IOrdemServicoSincronizador
{
    public async Task<ResultadoSincronizacao> SincronizarStatusAsync(Guid ordemServicoId, string status, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var repositorio = scope.ServiceProvider.GetRequiredService<IOrdemServicoRepository>();
        return await repositorio.SincronizarStatusAsync(ordemServicoId, status, agora, cancellationToken);
    }
}
