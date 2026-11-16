using System.Diagnostics.Metrics;
using AutoReparos.OS.Domain;

namespace AutoReparos.OS.Application.Saga;

/// <summary>
/// Métricas da Saga (D-66) exportadas pelo OTLP do OS: contadores de Sagas iniciadas, concluídas e compensações por
/// passo, e gauges de Sagas paradas e da profundidade da DLQ da Saga. Os gauges devolvem o último valor coletado por
/// <see cref="ColetorMetricasSaga"/>; sem coleta válida, não publicam amostra.
/// </summary>
public static class TelemetriaSaga
{
    public const string NomeMeter = "AutoReparos.OS.Saga";
    private const long SemAmostra = -1;

    private static readonly Meter Medidor = new(NomeMeter);
    private static readonly Counter<long> Iniciadas = Medidor.CreateCounter<long>("sagas_iniciadas_total", description: "Sagas iniciadas pelo orçamento aprovado.");
    private static readonly Counter<long> Concluidas = Medidor.CreateCounter<long>("sagas_concluidas_total", description: "Sagas concluídas com o pagamento capturado.");
    private static readonly Counter<long> Compensadas = Medidor.CreateCounter<long>("sagas_compensadas_total", description: "Comandos de compensação despachados, por passo.");
    private static long paradas = SemAmostra;
    private static long dlq = SemAmostra;

    static TelemetriaSaga()
    {
        Medidor.CreateObservableGauge("sagas_paradas", () => Observar(ref paradas), description: "Sagas não terminais sem atualização no limiar de parada.");
        Medidor.CreateObservableGauge("saga_dlq_mensagens", () => Observar(ref dlq), description: "Mensagens na DLQ da Saga (ApproximateNumberOfMessages).");
    }

    /// <summary>Conta o início (Saga nova) e a conclusão (passagem para <see cref="EstadoSaga.PagamentoCapturado"/>) depois de a Saga ser gravada.</summary>
    public static void RegistrarGravacao(EstadoSaga? estadoAnterior, EstadoSaga estadoAtual)
    {
        if (estadoAnterior is null) Iniciadas.Add(1);
        if (estadoAnterior != EstadoSaga.PagamentoCapturado && estadoAtual == EstadoSaga.PagamentoCapturado) Concluidas.Add(1);
    }

    /// <summary>Conta um comando de compensação despachado; o passo é o tipo do comando.</summary>
    public static void RegistrarCompensacao(string tipoComando) =>
        Compensadas.Add(1, new KeyValuePair<string, object?>("passo", tipoComando));

    /// <summary>Último número de Sagas paradas; <c>null</c> retira a amostra (falha na coleta).</summary>
    public static void AtualizarParadas(long? valor) => Interlocked.Exchange(ref paradas, valor ?? SemAmostra);

    /// <summary>Última profundidade da DLQ da Saga; <c>null</c> retira a amostra (falha na leitura da fila).</summary>
    public static void AtualizarDlq(long? valor) => Interlocked.Exchange(ref dlq, valor ?? SemAmostra);

    private static IEnumerable<Measurement<long>> Observar(ref long campo)
    {
        var valor = Interlocked.Read(ref campo);
        return valor == SemAmostra ? [] : [new Measurement<long>(valor)];
    }
}
