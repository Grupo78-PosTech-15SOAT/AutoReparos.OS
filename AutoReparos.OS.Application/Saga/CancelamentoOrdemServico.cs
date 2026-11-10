using AutoReparos.OS.Domain;

namespace AutoReparos.OS.Application.Saga;

public enum ResultadoCancelamento
{
    /// <summary>Cancelamento aceito (ou já aceito antes): repetir não tem novo efeito.</summary>
    Aceito,
    NaoEncontrada,
    /// <summary>A execução já concluiu ou o pagamento já foi capturado.</summary>
    Indisponivel
}

/// <param name="CorrelacaoId">Nulo quando a OS ainda não tem Saga (orçamento não aprovado).</param>
/// <param name="Estado">Estado da Saga (por exemplo <c>Compensando</c>) ou <c>Cancelada</c> quando a OS não tinha Saga.</param>
public sealed record RespostaCancelamento(ResultadoCancelamento Resultado, Guid OrdemServicoId, Guid? CorrelacaoId, string? Estado);

/// <summary>
/// Gatilho de produção da compensação: <c>POST /api/v1/ordens-servico/{id}/cancelamento</c>. OS sem Saga é marcada
/// <c>Cancelada</c> diretamente; OS com Saga passa pelo mesmo caminho de <c>ordem-servico.cancelar.v1</c>, com
/// <c>eventId</c> determinístico por OS (idempotente).
/// </summary>
public sealed class CancelamentoOrdemServico(ISagaRepositorio sagas, OrquestradorSaga orquestrador, IOrdemServicoRepository ordens, TimeProvider tempo)
{
    public async Task<RespostaCancelamento> CancelarAsync(Guid ordemServicoId, string motivo, CancellationToken cancellationToken)
    {
        var ordem = await ordens.ObterAsync(ordemServicoId, cancellationToken);
        if (ordem is null) return new RespostaCancelamento(ResultadoCancelamento.NaoEncontrada, ordemServicoId, null, null);

        if (await sagas.ObterAsync(ordemServicoId, cancellationToken) is null)
        {
            if (ordem.Status is not (StatusOS.Recebida or StatusOS.Cancelada))
                return new RespostaCancelamento(ResultadoCancelamento.Indisponivel, ordemServicoId, null, ordem.Status);
            // Sincronização idempotente: o mesmo status não duplica o histórico.
            await ordens.SincronizarStatusAsync(ordemServicoId, StatusOS.Cancelada, tempo.GetUtcNow(), cancellationToken);
            // Se a Saga nasceu entre a leitura e a gravação, o cancelamento segue pelo caminho da Saga.
            if (await sagas.ObterAsync(ordemServicoId, cancellationToken) is null)
                return new RespostaCancelamento(ResultadoCancelamento.Aceito, ordemServicoId, null, StatusOS.Cancelada);
        }

        var mensagem = new MensagemSaga(
            MensagemSaga.EventIdCancelamento(ordemServicoId), TiposMensagem.OrdemServicoCancelar, ordemServicoId, null, tempo.GetUtcNow(),
            null, null, null, null, null, motivo);
        await orquestrador.ProcessarAsync(mensagem, cancellationToken);

        var saga = await sagas.ObterAsync(ordemServicoId, cancellationToken)
            ?? throw new InvalidOperationException($"A Saga da OS {ordemServicoId} desapareceu durante o cancelamento.");
        var resultado = saga.Estado is EstadoSaga.ExecucaoConcluida or EstadoSaga.PagamentoCapturado
            ? ResultadoCancelamento.Indisponivel
            : ResultadoCancelamento.Aceito;
        return new RespostaCancelamento(resultado, ordemServicoId, saga.CorrelacaoId, saga.Estado.ToString());
    }
}
