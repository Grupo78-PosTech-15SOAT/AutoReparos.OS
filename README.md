# AutoReparos.OS

Serviço de ordens de serviço da Fase 4. Configure `ConnectionStrings__OrdemServico` com a conexão do banco próprio, `Authentication__Authority` e `Authentication__Audience` com os dados do emissor JWT, `AWS_REGION`, `Sqs__QueueUrl` e `Sqs__DeadLetterQueueUrl` antes de executar.

O serviço exige os papéis `Administrador`, `Atendente`, `Mecanico` ou `ServicoOS`. O emissor deve atribuir `ServicoOS` somente às identidades de serviço autorizadas.

Aplique as migrações com `dotnet ef database update --project AutoReparos.OS.Infrastructure --startup-project AutoReparos.OS.Api` usando as credenciais de migração apropriadas. Configure a conexão por variável de ambiente; não armazene credenciais no repositório.

Os eventos são gravados na mesma transação da OS. O publicador tenta a fila principal cinco vezes, com intervalo exponencial e jitter, e encaminha o evento à DLQ. Uma falha no envio à DLQ mantém o evento pendente e registra erro para intervenção. A entrega é pelo menos uma vez: os consumidores devem deduplicar pelo `eventId`. Configure alertas para os erros de publicação e a DLQ no ambiente de observabilidade.

## Prova de isolamento

O teste `ServiceDatabaseIsolation` consulta os privilégios efetivos dos papéis de Billing e Execução e acessa uma OS com o token de cada serviço. Para executá-lo em um ambiente de teste provisionado, configure `OS_PROOF_DB_CONNECTION`, `OS_PROOF_API_URL`, `OS_PROOF_ORDER_ID`, `OS_PROOF_BILLING_DB_ROLE`, `OS_PROOF_EXECUTION_DB_ROLE`, `OS_PROOF_BILLING_TOKEN` e `OS_PROOF_EXECUTION_TOKEN` no ambiente do processo. A conexão de prova deve poder consultar os privilégios dos papéis. Use uma OS de teste e identidades representativas do ambiente; não registre os tokens ou a conexão em arquivos versionados.
