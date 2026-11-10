using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace AutoReparos.OS.Infrastructure.Saga;

/// <summary>
/// Cria as tabelas da Saga se não existirem. Destinado a ambiente local e de teste (DynamoDB Local); o provisionamento
/// real (capacidade, backup, TTL, alarmes) pertence à feature de infraestrutura.
/// </summary>
public sealed class DynamoDbInicializador(IAmazonDynamoDB dynamo, OpcoesDynamoDb opcoes)
{
    public async Task GarantirTabelasAsync(CancellationToken cancellationToken)
    {
        await CriarSeNecessarioAsync(new CreateTableRequest
        {
            TableName = opcoes.TabelaSaga,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema = [new KeySchemaElement("ordemServicoId", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("ordemServicoId", ScalarAttributeType.S),
                new AttributeDefinition("trabalhoPendente", ScalarAttributeType.S),
                new AttributeDefinition("proximaTentativaEm", ScalarAttributeType.S)
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = OpcoesDynamoDb.IndicePendentes,
                    KeySchema = [new KeySchemaElement("trabalhoPendente", KeyType.HASH), new KeySchemaElement("proximaTentativaEm", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY }
                }
            ]
        }, cancellationToken);

        await CriarSeNecessarioAsync(new CreateTableRequest
        {
            TableName = opcoes.TabelaEventos,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema = [new KeySchemaElement("eventId", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("eventId", ScalarAttributeType.S)]
        }, cancellationToken);

        var ttl = await dynamo.DescribeTimeToLiveAsync(opcoes.TabelaEventos, cancellationToken);
        if (ttl.TimeToLiveDescription?.TimeToLiveStatus != TimeToLiveStatus.ENABLED && ttl.TimeToLiveDescription?.TimeToLiveStatus != TimeToLiveStatus.ENABLING)
        {
            await dynamo.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
            {
                TableName = opcoes.TabelaEventos,
                TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = "expiraEm" }
            }, cancellationToken);
        }
    }

    private async Task CriarSeNecessarioAsync(CreateTableRequest requisicao, CancellationToken cancellationToken)
    {
        try
        {
            await dynamo.CreateTableAsync(requisicao, cancellationToken);
        }
        catch (ResourceInUseException)
        {
            // A tabela já existe (ou outra instância a está criando): só aguarda ficar ativa.
        }

        for (var i = 0; i < 100; i++)
        {
            var descricao = await dynamo.DescribeTableAsync(requisicao.TableName, cancellationToken);
            if (descricao.Table.TableStatus == TableStatus.ACTIVE) return;
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
        throw new TimeoutException($"A tabela {requisicao.TableName} não ficou ativa a tempo.");
    }
}
