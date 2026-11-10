namespace AutoReparos.OS.Domain;

public static class StatusOS
{
    public const string Recebida = "Recebida";
    public const string OrcamentoAprovado = "OrcamentoAprovado";
    public const string EmExecucao = "EmExecucao";
    public const string Concluida = "Concluida";
    public const string Paga = "Paga";
    public const string IntervencaoRequerida = "IntervencaoRequerida";
    public const string Cancelada = "Cancelada";

    public static readonly IReadOnlyList<string> Todos =
        [Recebida, OrcamentoAprovado, EmExecucao, Concluida, Paga, IntervencaoRequerida, Cancelada];
}
