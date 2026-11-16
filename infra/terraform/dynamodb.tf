# Estado da Saga orquestrada (um item por ordemServicoId) e deduplicação de eventos por eventId.
# O esquema é o mesmo que o DynamoDbInicializador cria no ambiente local.
resource "aws_dynamodb_table" "saga" {
  name         = "autoreparos-os-saga"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "ordemServicoId"

  attribute {
    name = "ordemServicoId"
    type = "S"
  }

  attribute {
    name = "trabalhoPendente"
    type = "S"
  }

  attribute {
    name = "proximaTentativaEm"
    type = "S"
  }

  global_secondary_index {
    name            = "trabalho-pendente-index"
    hash_key        = "trabalhoPendente"
    range_key       = "proximaTentativaEm"
    projection_type = "KEYS_ONLY"
  }
}

resource "aws_dynamodb_table" "saga_eventos" {
  name         = "autoreparos-os-saga-eventos"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "eventId"

  attribute {
    name = "eventId"
    type = "S"
  }

  ttl {
    attribute_name = "expiraEm"
    enabled        = true
  }
}
