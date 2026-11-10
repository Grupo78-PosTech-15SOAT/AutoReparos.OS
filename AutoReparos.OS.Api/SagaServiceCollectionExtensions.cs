using Amazon.DynamoDBv2;
using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Infrastructure.Saga;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class SagaServiceCollectionExtensions
{
    /// <summary>
    /// Registra a Saga orquestrada. Os serviços de fila e de DynamoDB só são criados quando <c>Sqs:SagaQueueUrl</c>
    /// está configurada (os hosted services não resolvem nada antes disso).
    /// </summary>
    public static IServiceCollection AddSagaOS(this IServiceCollection services, IConfiguration configuration)
    {
        var dynamo = configuration.GetSection("DynamoDb");
        var padrao = new OpcoesDynamoDb();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new OpcoesDynamoDb
        {
            TabelaSaga = dynamo["TabelaSaga"] ?? padrao.TabelaSaga,
            TabelaEventos = dynamo["TabelaEventos"] ?? padrao.TabelaEventos
        });
        services.AddSingleton<IAmazonDynamoDB>(_ => dynamo["ServiceUrl"] is { Length: > 0 } url
            ? new AmazonDynamoDBClient(new AmazonDynamoDBConfig { ServiceURL = url, AuthenticationRegion = configuration["AWS_REGION"] ?? "us-east-1" })
            : new AmazonDynamoDBClient());
        services.AddSingleton<ISagaRepositorio, DynamoDbSagaRepositorio>();
        services.AddSingleton<DynamoDbInicializador>();
        services.AddSingleton<IOrdemServicoSincronizador, SincronizadorOrdemServico>();
        services.AddSingleton<IEnvioComandos, SqsEnvioComandos>();
        services.AddSingleton<IAlertador, AlertadorLog>();
        var prazo = configuration.GetValue<TimeSpan?>("Saga:PrazoCompensacao");
        if (prazo is { } p && p <= TimeSpan.Zero) throw new InvalidOperationException("Saga:PrazoCompensacao deve ser maior que zero.");
        services.AddSingleton(prazo is { } valor ? new OpcoesSaga { PrazoCompensacao = valor } : new OpcoesSaga());
        services.AddScoped<CancelamentoOrdemServico>();
        services.AddSingleton<OrquestradorSaga>();
        services.AddSingleton<ProcessadorMensagemSaga>();
        services.AddSingleton<DespachadorSaga>();
        services.AddHostedService<SagaConsumidor>();
        services.AddHostedService<SagaDespachador>();
        return services;
    }
}
