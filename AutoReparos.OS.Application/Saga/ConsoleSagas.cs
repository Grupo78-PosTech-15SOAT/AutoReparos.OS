using AutoReparos.OS.Domain;

namespace AutoReparos.OS.Application.Saga;

/// <summary>Console administrativo da Saga (D-18d): consulta, lista Sagas paradas e retenta a reserva; cada ação fica no histórico da própria Saga.</summary>
public sealed class ConsoleSagas(ISagaRepositorio sagas, OrquestradorSaga orquestrador, TimeProvider tempo)
{
    public async Task<IReadOnlyList<SagaOS>> ListarAsync(EstadoSaga? estado, int? paradaHaMinutos, CancellationToken cancellationToken) =>
        await sagas.ListarAsync(estado, paradaHaMinutos is { } minutos ? tempo.GetUtcNow() - TimeSpan.FromMinutes(minutos) : null, cancellationToken);

    public Task<SagaOS?> ObterAsync(Guid ordemServicoId, CancellationToken cancellationToken) => sagas.ObterAsync(ordemServicoId, cancellationToken);

    public Task<ResultadoRetentativa> RetentarAsync(Guid ordemServicoId, string autor, string motivo, CancellationToken cancellationToken) =>
        orquestrador.RetentarReservaAsync(ordemServicoId, autor, motivo, cancellationToken);
}
