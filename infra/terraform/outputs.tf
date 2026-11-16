output "ecr_url" {
  description = "URL do repositório ECR do serviço (a tag da imagem é o SHA do commit)."
  value       = aws_ecr_repository.servico.repository_url
}

output "irsa_role_arn" {
  description = "ARN da role IRSA anotada no ServiceAccount do chart."
  value       = aws_iam_role.irsa.arn
}

output "segredo_database_arn" {
  description = "ARN do segredo com a connection string do serviço."
  value       = aws_secretsmanager_secret.database.arn
}

output "fila_saga_url" {
  description = "URL da fila de entrada da Saga."
  value       = aws_sqs_queue.saga.url
}

output "fila_saga_dlq_url" {
  description = "URL da DLQ da Saga."
  value       = aws_sqs_queue.saga_dlq.url
}

output "tabela_saga" {
  description = "Nome da tabela DynamoDB da Saga."
  value       = aws_dynamodb_table.saga.name
}

output "tabela_saga_eventos" {
  description = "Nome da tabela DynamoDB de eventos processados da Saga."
  value       = aws_dynamodb_table.saga_eventos.name
}
