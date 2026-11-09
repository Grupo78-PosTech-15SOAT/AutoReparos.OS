using AutoReparos.OS.Domain;

namespace AutoReparos.OS.Application;

public interface IOrdemServicoRepository
{
    Task AdicionarAsync(OrdemServico ordem, CancellationToken cancellationToken);
    Task<OrdemServico?> ObterAsync(Guid id, CancellationToken cancellationToken);
    Task SalvarAsync(CancellationToken cancellationToken);
}
