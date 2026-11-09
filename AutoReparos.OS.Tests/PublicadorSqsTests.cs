using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.OS.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Testcontainers.PostgreSql;

public sealed class PublicadorSqsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer db = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private ServiceProvider services = null!;
    public async Task InitializeAsync()
    {
        await db.StartAsync();
        services = new ServiceCollection().AddDbContext<OrdemServicoDbContext>(o => o.UseNpgsql(db.GetConnectionString())).BuildServiceProvider();
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>().Database.MigrateAsync();
    }
    public async Task DisposeAsync() { await services.DisposeAsync(); await db.DisposeAsync(); }

    [Theory]
    [InlineData(0, false, "principal", false)]
    [InlineData(0, true, "principal", false)]
    [InlineData(5, false, "falhas", true)]
    [InlineData(5, true, "falhas", false)]
    public async Task PublishesOrPreservesEvent(int previousFailures, bool sendFails, string expectedQueue, bool expectedDlq)
    {
        var id = Guid.NewGuid();
        using (var scope = services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>();
            context.MensagensPendentes.Add(new MensagemPendente { Id = id, Conteudo = "{\"eventId\":\"" + id + "\"}", CriadoEm = DateTimeOffset.UtcNow, Tentativas = previousFailures });
            await context.SaveChangesAsync();
        }
        var sqs = new Mock<IAmazonSQS>();
        var request = sqs.Setup(s => s.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()));
        if (sendFails) request.ThrowsAsync(new AmazonSQSException("indisponível"));
        else request.ReturnsAsync(new SendMessageResponse());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sqs:QueueUrl"] = "principal", ["Sqs:DeadLetterQueueUrl"] = "falhas" }).Build();
        using var worker = new PublicadorSqs(services.GetRequiredService<IServiceScopeFactory>(), sqs.Object, config, NullLogger<PublicadorSqs>.Instance);
        await worker.StartAsync(CancellationToken.None);
        MensagemPendente? row = null;
        try
        {
            for (var i = 0; i < 100; i++)
            {
                using var scope = services.CreateScope();
                row = await scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>().MensagensPendentes.AsNoTracking().SingleAsync(x => x.Id == id);
                if (row.EnviadoEm is not null || row.Tentativas > previousFailures) break;
                await Task.Delay(100);
            }
        }
        finally { await worker.StopAsync(CancellationToken.None); }
        Assert.NotNull(row);
        sqs.Verify(s => s.SendMessageAsync(It.Is<SendMessageRequest>(r => r.QueueUrl == expectedQueue && r.MessageBody.Contains(id.ToString())), It.IsAny<CancellationToken>()), Times.Once);
        if (sendFails)
        {
            Assert.Null(row.EnviadoEm);
            Assert.Equal(previousFailures + 1, row.Tentativas);
            Assert.True(row.ProximaTentativaEm > DateTimeOffset.UtcNow);
        }
        else
        {
            Assert.NotNull(row.EnviadoEm);
            Assert.Equal(expectedDlq, row.EncaminhadoParaDlq);
        }
    }
}
