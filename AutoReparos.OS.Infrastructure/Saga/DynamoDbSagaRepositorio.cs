using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;

namespace AutoReparos.OS.Infrastructure.Saga;

/// <summary>
/// Persistência da Saga em DynamoDB. Tabela da Saga: um item por <c>ordemServicoId</c>, com o documento completo e os
/// atributos de consulta (<c>estado</c>, <c>versao</c>, <c>correlacaoId</c>); a escrita é condicional por versão.
/// Tabela de eventos: um item por <c>eventId</c> processado, gravado na mesma <c>TransactWriteItems</c> da transição.
/// O índice esparso <see cref="OpcoesDynamoDb.IndicePendentes"/> só contém Sagas com trabalho pendente.
/// </summary>
public sealed class DynamoDbSagaRepositorio(IAmazonDynamoDB dynamo, OpcoesDynamoDb opcoes, TimeProvider tempo) : ISagaRepositorio
{
    internal const string Pendente = "S";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<SagaOS?> ObterAsync(Guid ordemServicoId, CancellationToken cancellationToken)
    {
        var resposta = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = opcoes.TabelaSaga,
            ConsistentRead = true,
            Key = new Dictionary<string, AttributeValue> { ["ordemServicoId"] = new() { S = ordemServicoId.ToString() } }
        }, cancellationToken);
        if (resposta.Item is not { Count: > 0 } item) return null;
        var saga = JsonSerializer.Deserialize<SagaOS>(item["documento"].S, Json) ?? throw new InvalidOperationException("Documento da Saga vazio.");
        saga.AplicarVersao(long.Parse(item["versao"].N, CultureInfo.InvariantCulture));
        return saga;
    }

    public async Task<bool> EventoProcessadoAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var resposta = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = opcoes.TabelaEventos,
            ConsistentRead = true,
            Key = new Dictionary<string, AttributeValue> { ["eventId"] = new() { S = eventId.ToString() } },
            ProjectionExpression = "eventId"
        }, cancellationToken);
        return resposta.Item is { Count: > 0 };
    }

    public async Task<ResultadoGravacao> GravarAsync(SagaOS saga, EventoProcessado? evento, CancellationToken cancellationToken)
    {
        var esperada = saga.Versao;
        var put = new Put
        {
            TableName = opcoes.TabelaSaga,
            Item = Item(saga, esperada + 1),
            ConditionExpression = esperada == 0 ? "attribute_not_exists(ordemServicoId)" : "versao = :esperada",
        };
        if (esperada != 0) put.ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":esperada"] = new() { N = esperada.ToString(CultureInfo.InvariantCulture) } };
        var itens = new List<TransactWriteItem> { new() { Put = put } };
        if (evento is not null)
        {
            var agora = tempo.GetUtcNow();
            itens.Add(new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = opcoes.TabelaEventos,
                    ConditionExpression = "attribute_not_exists(eventId)",
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["eventId"] = new() { S = evento.EventId.ToString() },
                        ["tipo"] = new() { S = evento.Tipo },
                        ["ordemServicoId"] = new() { S = saga.OrdemServicoId.ToString() },
                        ["processadoEm"] = new() { S = Formatar(agora) },
                        ["expiraEm"] = new() { N = (agora + opcoes.RetencaoEventos).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) }
                    }
                }
            });
        }

        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = itens }, cancellationToken);
        }
        catch (TransactionCanceledException ex)
        {
            var motivos = ex.CancellationReasons ?? [];
            if (evento is not null && motivos.Count > 1 && motivos[1].Code == "ConditionalCheckFailed") return ResultadoGravacao.EventoDuplicado;
            if (motivos.Count > 0 && motivos[0].Code is "ConditionalCheckFailed" or "TransactionConflict") return ResultadoGravacao.ConflitoVersao;
            if (motivos.Any(m => m.Code == "TransactionConflict")) return ResultadoGravacao.ConflitoVersao;
            throw;
        }
        saga.AplicarVersao(esperada + 1);
        return ResultadoGravacao.Gravada;
    }

    public async Task<IReadOnlyList<Guid>> ListarComTrabalhoPendenteAsync(DateTimeOffset ate, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        Dictionary<string, AttributeValue>? proximo = null;
        do
        {
            var resposta = await dynamo.QueryAsync(new QueryRequest
            {
                TableName = opcoes.TabelaSaga,
                IndexName = OpcoesDynamoDb.IndicePendentes,
                KeyConditionExpression = "trabalhoPendente = :p AND proximaTentativaEm <= :ate",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":p"] = new() { S = Pendente }, [":ate"] = new() { S = Formatar(ate) } },
                ExclusiveStartKey = proximo
            }, cancellationToken);
            ids.AddRange(resposta.Items.Select(i => Guid.Parse(i["ordemServicoId"].S)));
            proximo = resposta.LastEvaluatedKey is { Count: > 0 } ? resposta.LastEvaluatedKey : null;
        } while (proximo is not null);
        return ids;
    }

    private static Dictionary<string, AttributeValue> Item(SagaOS saga, long versao)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["ordemServicoId"] = new() { S = saga.OrdemServicoId.ToString() },
            ["correlacaoId"] = new() { S = saga.CorrelacaoId.ToString() },
            ["estado"] = new() { S = saga.Estado.ToString() },
            ["versao"] = new() { N = versao.ToString(CultureInfo.InvariantCulture) },
            ["atualizadoEm"] = new() { S = Formatar(saga.AtualizadoEm) },
            ["documento"] = new() { S = JsonSerializer.Serialize(saga, Json) }
        };
        if (saga.ProximoTrabalhoEm() is { } quando)
        {
            item["trabalhoPendente"] = new AttributeValue { S = Pendente };
            item["proximaTentativaEm"] = new AttributeValue { S = Formatar(quando) };
        }
        return item;
    }

    /// <summary>ISO 8601 UTC de largura fixa: a ordem lexicográfica é a ordem cronológica.</summary>
    internal static string Formatar(DateTimeOffset instante) =>
        instante.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
