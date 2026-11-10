using AutoReparos.OS.Domain;

namespace AutoReparos.OS.Application;

public interface IOrdemServicoRepository
{
    Task AdicionarAsync(OrdemServico ordem, CancellationToken cancellationToken);
    Task<OrdemServico?> ObterAsync(Guid id, CancellationToken cancellationToken);
    Task SalvarAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Define o status da OS e acrescenta histórico somente se ele mudou (idempotente), com a concorrência otimista da OS.
    /// </summary>
    Task<ResultadoSincronizacao> SincronizarStatusAsync(Guid id, string status, DateTimeOffset agora, CancellationToken cancellationToken);
}

/// <param name="Encontrada">A OS existe no Postgres.</param>
/// <param name="Alterada">O status mudou nesta chamada (e o histórico ganhou um registro).</param>
public sealed record ResultadoSincronizacao(bool Encontrada, bool Alterada, Guid ClienteId, Guid VeiculoId);
