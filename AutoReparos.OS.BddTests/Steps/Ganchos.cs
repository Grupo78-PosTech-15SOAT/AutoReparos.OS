using Reqnroll;
using Reqnroll.BoDi;

/// <summary>Containers compartilhados (uma vez por execução) e ciclo de vida do <see cref="ContextoSaga"/> por cenário.</summary>
[Binding]
public sealed class Ganchos(IObjectContainer container)
{
    private static readonly SagaAmbiente Ambiente = new();

    [BeforeTestRun]
    public static Task IniciarContainersAsync() => Ambiente.InitializeAsync();

    [AfterTestRun]
    public static Task EncerrarContainersAsync() => Ambiente.DisposeAsync();

    [BeforeScenario(Order = 0)]
    public async Task IniciarCenarioAsync()
    {
        var contexto = new ContextoSaga(Ambiente);
        await contexto.InitializeAsync();
        container.RegisterInstanceAs(contexto);
    }

    [AfterScenario]
    public Task EncerrarCenarioAsync() => container.Resolve<ContextoSaga>().DisposeAsync();
}
