using System.Net.Http.Headers;
using System.Text.Json;
using Npgsql;

public sealed class ServiceDatabaseIsolationTests
{
    [Fact]
    public async Task ServiceDatabaseIsolation()
    {
        var connection = Required("OS_PROOF_DB_CONNECTION");
        var api = new Uri(Required("OS_PROOF_API_URL"), UriKind.Absolute);
        var orderId = Guid.Parse(Required("OS_PROOF_ORDER_ID"));
        var services = new[]
        {
            (Role: Required("OS_PROOF_BILLING_DB_ROLE"), Token: Required("OS_PROOF_BILLING_TOKEN")),
            (Role: Required("OS_PROOF_EXECUTION_DB_ROLE"), Token: Required("OS_PROOF_EXECUTION_TOKEN"))
        };

        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        using var client = new HttpClient { BaseAddress = api };
        foreach (var service in services)
        {
            await using var command = new NpgsqlCommand("SELECT has_database_privilege(@role, current_database(), 'CONNECT'), has_table_privilege(@role, 'public.ordens_servico', 'SELECT'), has_table_privilege(@role, 'public.historico_os', 'SELECT'), has_table_privilege(@role, 'public.mensagens_pendentes', 'SELECT')", db);
            command.Parameters.AddWithValue("role", service.Role);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var canConnect = reader.GetBoolean(0);
            Assert.False(canConnect && reader.GetBoolean(1), $"{service.Role} pode ler ordens_servico diretamente.");
            Assert.False(canConnect && reader.GetBoolean(2), $"{service.Role} pode ler historico_os diretamente.");
            Assert.False(canConnect && reader.GetBoolean(3), $"{service.Role} pode ler mensagens_pendentes diretamente.");
            await reader.CloseAsync();

            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/ordens-servico/{orderId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", service.Token);
            using var response = await client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, $"A consulta da identidade {service.Role} retornou {(int)response.StatusCode}.");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(orderId, body.RootElement.GetProperty("id").GetGuid());
        }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Variável de prova {name} não configurada.");
}
