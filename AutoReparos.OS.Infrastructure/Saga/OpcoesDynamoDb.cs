namespace AutoReparos.OS.Infrastructure.Saga;

public sealed class OpcoesDynamoDb
{
    public const string IndicePendentes = "trabalho-pendente-index";

    public string TabelaSaga { get; init; } = "autoreparos-os-saga";
    public string TabelaEventos { get; init; } = "autoreparos-os-saga-eventos";
    /// <summary>Quanto tempo o registro de deduplicação é mantido (TTL); deve superar a retenção da fila (14 dias no SQS).</summary>
    public TimeSpan RetencaoEventos { get; init; } = TimeSpan.FromDays(30);
}
