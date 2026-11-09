using AutoReparos.OS.Application;
using AutoReparos.OS.Domain;
using AutoReparos.OS.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Amazon.SQS;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var serviceName = builder.Configuration["Telemetry:ServiceName"] ?? "autoreparos-os";
var otlpEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"] ?? "http://localhost:4317";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(serviceName)
        .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", builder.Environment.EnvironmentName)]))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint)))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint)));
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    logging.ParseStateValues = true;
    logging.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
});
builder.Services.AddOpenApi();
builder.Services.AddDbContext<OrdemServicoDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("OrdemServico")));
builder.Services.AddScoped<IOrdemServicoRepository>(services => services.GetRequiredService<OrdemServicoDbContext>());
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = builder.Configuration["Authentication:Authority"];
    options.Audience = builder.Configuration["Authentication:Audience"];
    options.RequireHttpsMetadata = builder.Configuration.GetValue("Authentication:RequireHttpsMetadata", true);
});
builder.Services.AddAuthorization(options => options.AddPolicy("OperarOS", policy =>
    policy.RequireRole("Administrador", "Atendente", "Mecanico", "ServicoOS")));
builder.Services.AddSingleton<IAmazonSQS, AmazonSQSClient>();
builder.Services.AddHostedService<PublicadorSqs>();
var app = builder.Build();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseAuthentication();
app.UseAuthorization();
var ordens = app.MapGroup("/api/v1/ordens-servico").RequireAuthorization("OperarOS");
ordens.MapPost("/", async (CriarOrdem request, IOrdemServicoRepository repository, CancellationToken ct) =>
{
    if (request.ClienteId == Guid.Empty || request.VeiculoId == Guid.Empty || request.Observacao?.Length > 2000)
        return Results.UnprocessableEntity(new { codigo = "ordem_servico_invalida" });
    var ordem = new OrdemServico(request.ClienteId, request.VeiculoId, request.Observacao, DateTimeOffset.UtcNow);
    await repository.AdicionarAsync(ordem, ct);
    await repository.SalvarAsync(ct);
    return Results.Created($"/api/v1/ordens-servico/{ordem.Id}", new { ordem.Id, ordem.Status });
});
ordens.MapGet("/{id:guid}", async (Guid id, IOrdemServicoRepository repository, CancellationToken ct) =>
{
    var ordem = await repository.ObterAsync(id, ct);
    return ordem is null ? Results.NotFound() : Results.Ok(new { ordem.Id, ordem.ClienteId, ordem.VeiculoId, ordem.Observacao, ordem.Status, Historico = ordem.Historico.OrderBy(h => h.RegistradoEm).ThenBy(h => h.Id) });
});
ordens.MapPatch("/{id:guid}", async (Guid id, AtualizarOrdem request, IOrdemServicoRepository repository, CancellationToken ct) =>
{
    if (request.Observacao?.Length > 2000) return Results.UnprocessableEntity(new { codigo = "ordem_servico_invalida" });
    var ordem = await repository.ObterAsync(id, ct);
    if (ordem is null) return Results.NotFound();
    ordem.AtualizarObservacao(request.Observacao);
    await repository.SalvarAsync(ct);
    return Results.Ok(new { ordem.Id, ordem.Status, ordem.Observacao });
});
app.Run();
public sealed record CriarOrdem(Guid ClienteId, Guid VeiculoId, string? Observacao);
public sealed record AtualizarOrdem(string? Observacao);
public partial class Program;
