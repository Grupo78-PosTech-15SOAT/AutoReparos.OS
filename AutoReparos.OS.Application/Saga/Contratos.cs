using AutoReparos.OS.Domain;

namespace AutoReparos.OS.Application.Saga;

public enum ResultadoGravacao
{
    Gravada,
    /// <summary>A versão esperada não é mais a persistida; recarregar e refazer.</summary>
    ConflitoVersao,
    /// <summary>O <c>eventId</c> já estava registrado como processado.</summary>
    EventoDuplicado
}

/// <summary>Registro persistido de um evento processado (deduplicação por <c>eventId</c>).</summary>
public sealed record EventoProcessado(Guid EventId, string Tipo);

public interface ISagaRepositorio
{
    Task<SagaOS?> ObterAsync(Guid ordemServicoId, CancellationToken cancellationToken);

    Task<bool> EventoProcessadoAsync(Guid eventId, CancellationToken cancellationToken);

    /// <summary>
    /// Grava a Saga com condição de versão (<see cref="SagaOS.Versao"/> esperada; 0 cria). Com <paramref name="evento"/>, o registro
    /// de deduplicação entra na mesma transação, assim como os comandos e sincronizações pendentes que fazem parte da Saga.
    /// </summary>
    Task<ResultadoGravacao> GravarAsync(SagaOS saga, EventoProcessado? evento, CancellationToken cancellationToken);

    /// <summary>Ids das Sagas com sincronização ou comando pendente devido até <paramref name="ate"/>.</summary>
    Task<IReadOnlyList<Guid>> ListarComTrabalhoPendenteAsync(DateTimeOffset ate, CancellationToken cancellationToken);
}

public interface IEnvioComandos
{
    Task EnviarAsync(DestinoComando destino, string corpo, CancellationToken cancellationToken);

    /// <summary>Envia o corpo original à DLQ da Saga, com o motivo.</summary>
    Task EnviarParaDlqAsync(string corpo, string motivo, CancellationToken cancellationToken);
}

public interface IAlertador
{
    void Alertar(string codigo, string mensagem, Guid? ordemServicoId = null, Guid? eventId = null);
}

/// <summary>Sincroniza o status da OS no Postgres (um escopo de banco por chamada).</summary>
public interface IOrdemServicoSincronizador
{
    Task<ResultadoSincronizacao> SincronizarStatusAsync(Guid ordemServicoId, string status, DateTimeOffset agora, CancellationToken cancellationToken);
}

public sealed class SagaInexistenteException(Guid ordemServicoId, string tipo)
    : Exception($"Não existe Saga para a OS {ordemServicoId}; a mensagem {tipo} chegou antes de orcamento.aprovado.v1.");

public sealed class ConflitoSagaException(Guid ordemServicoId)
    : Exception($"Conflito persistente de versão ao gravar a Saga da OS {ordemServicoId}.");
