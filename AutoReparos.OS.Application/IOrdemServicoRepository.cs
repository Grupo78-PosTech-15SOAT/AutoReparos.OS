using AutoReparos.OS.Domain;

namespace AutoReparos.OS.Application;

public interface IOrdemServicoRepository
{
    Task AdicionarAsync(OrdemServico ordem, CancellationToken cancellationToken);
    Task<OrdemServico?> ObterAsync(Guid id, CancellationToken cancellationToken);
    Task SalvarAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lista as OS, da atualizada há menos tempo para a mais antiga, opcionalmente filtradas por status; <paramref name="pagina"/> começa em 1.
    /// </summary>
    Task<PaginaOS> ListarAsync(string? status, int pagina, int tamanho, CancellationToken cancellationToken);

    /// <summary>
    /// Define o status da OS e acrescenta histórico somente se ele mudou (idempotente), com a concorrência otimista da OS.
    /// </summary>
    Task<ResultadoSincronizacao> SincronizarStatusAsync(Guid id, string status, DateTimeOffset agora, CancellationToken cancellationToken);
}

/// <param name="AtualizadaEm">Momento do último registro do histórico (a OS muda de status sempre com um registro novo).</param>
public sealed record ItemListaOS(Guid Id, Guid ClienteId, Guid VeiculoId, string Status, DateTimeOffset AtualizadaEm);

/// <param name="Total">Quantidade de OS que atendem ao filtro, independentemente da página.</param>
public sealed record PaginaOS(int Total, IReadOnlyList<ItemListaOS> Itens);

/// <param name="Encontrada">A OS existe no Postgres.</param>
/// <param name="Alterada">O status mudou nesta chamada (e o histórico ganhou um registro).</param>
public sealed record ResultadoSincronizacao(bool Encontrada, bool Alterada, Guid ClienteId, Guid VeiculoId);
