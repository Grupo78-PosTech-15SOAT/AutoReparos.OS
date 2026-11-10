namespace AutoReparos.OS.Application.Saga;

/// <summary>Política técnica da Saga: 5 tentativas, backoff exponencial com jitter limitado (mesma política dos demais serviços).</summary>
public sealed class OpcoesSaga
{
    public int MaxTentativas { get; init; } = 5;
    public TimeSpan AtrasoBase { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan AtrasoMaximo { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Teto do atraso entre tentativas de sincronização da OS (recuperação para frente nunca desiste).</summary>
    public TimeSpan AtrasoSincronizacaoMaximo { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>O jitter soma até esta fração do atraso exponencial.</summary>
    public double FatorJitter { get; init; } = 0.5;
    /// <summary>Prazo para a confirmação de cada compensação; vencido, a Saga vai a <c>CompensacaoFalhou</c> com alerta (<c>Saga:PrazoCompensacao</c>).</summary>
    public TimeSpan PrazoCompensacao { get; init; } = TimeSpan.FromMinutes(15);
    public int MaxConflitosVersao { get; init; } = 10;
    public Func<double> Aleatorio { get; init; } = () => Random.Shared.NextDouble();
    public Func<TimeSpan, CancellationToken, Task> Aguardar { get; init; } = (atraso, ct) => Task.Delay(atraso, ct);

    /// <summary>Atraso após a falha de número <paramref name="tentativa"/> (1 = primeira): base·2^(n-1), limitado, mais jitter.</summary>
    public TimeSpan Atraso(int tentativa, TimeSpan? teto = null)
    {
        var exponencial = AtrasoBase.TotalSeconds * Math.Pow(2, Math.Min(Math.Max(tentativa, 1) - 1, 30));
        var limitado = Math.Min(exponencial, (teto ?? AtrasoMaximo).TotalSeconds);
        return TimeSpan.FromSeconds(limitado * (1 + FatorJitter * Aleatorio()));
    }
}
