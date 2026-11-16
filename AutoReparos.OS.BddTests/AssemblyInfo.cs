// Os cenários compartilham os containers (PostgreSQL e DynamoDB Local) iniciados uma vez por execução.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
