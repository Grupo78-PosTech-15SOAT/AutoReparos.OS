# Filas de entrada do OS (a fila pertence ao consumidor). Cobrança e Execução publicam na fila da Saga.
resource "aws_sqs_queue" "saga_dlq" {
  name                      = "autoreparos-os-saga-dlq"
  message_retention_seconds = 1209600
  sqs_managed_sse_enabled   = true
}

resource "aws_sqs_queue" "saga" {
  name                       = "autoreparos-os-saga"
  visibility_timeout_seconds = 90
  message_retention_seconds  = 345600
  receive_wait_time_seconds  = 20
  sqs_managed_sse_enabled    = true
  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.saga_dlq.arn
    maxReceiveCount     = 5
  })
}
