# C12: tabelas da Saga do OS (eventos com TTL) e política IRSA do OS limitada a elas no DynamoDB.

mock_provider "aws" {
  mock_data "aws_caller_identity" {
    defaults = {
      account_id = "198903849859"
    }
  }
}

override_data {
  target = data.aws_ssm_parameter.oidc_provider_arn
  values = {
    insecure_value = "arn:aws:iam::198903849859:oidc-provider/oidc.eks.us-east-1.amazonaws.com/id/EXEMPLO"
  }
}

override_data {
  target = data.aws_ssm_parameter.oidc_provider_url
  values = {
    insecure_value = "oidc.eks.us-east-1.amazonaws.com/id/EXEMPLO"
  }
}

override_data {
  target = data.aws_ssm_parameter.database_address
  values = {
    insecure_value = "autoreparos-db.exemplo.us-east-1.rds.amazonaws.com"
  }
}

run "tabelas_da_saga" {
  command = apply

  assert {
    condition     = aws_dynamodb_table.saga.name == "autoreparos-os-saga" && aws_dynamodb_table.saga.hash_key == "ordemServicoId"
    error_message = "A tabela da Saga é autoreparos-os-saga, com chave ordemServicoId."
  }

  assert {
    condition     = one([for indice in aws_dynamodb_table.saga.global_secondary_index : indice.name]) == "trabalho-pendente-index"
    error_message = "A tabela da Saga precisa do índice trabalho-pendente-index que o repositório consulta."
  }

  assert {
    condition     = aws_dynamodb_table.saga_eventos.name == "autoreparos-os-saga-eventos" && aws_dynamodb_table.saga_eventos.hash_key == "eventId"
    error_message = "A tabela de eventos é autoreparos-os-saga-eventos, com chave eventId."
  }

  assert {
    condition     = aws_dynamodb_table.saga_eventos.ttl[0].enabled && aws_dynamodb_table.saga_eventos.ttl[0].attribute_name == "expiraEm"
    error_message = "A tabela de eventos precisa de TTL ligado no atributo expiraEm."
  }

  assert {
    condition = alltrue(flatten([
      for declaracao in jsondecode(aws_iam_role_policy.irsa.policy).Statement : [
        for recurso in declaracao.Resource : contains([
          "arn:aws:dynamodb:us-east-1:198903849859:table/autoreparos-os-saga",
          "arn:aws:dynamodb:us-east-1:198903849859:table/autoreparos-os-saga/index/*",
          "arn:aws:dynamodb:us-east-1:198903849859:table/autoreparos-os-saga-eventos",
        ], recurso)
      ] if anytrue([for acao in declaracao.Action : startswith(acao, "dynamodb:")])
    ]))
    error_message = "A política IRSA do OS só pode alcançar no DynamoDB as tabelas da Saga."
  }

  assert {
    condition = alltrue([
      for tabela in ["table/autoreparos-os-saga", "table/autoreparos-os-saga-eventos"] : contains(flatten([
        for declaracao in jsondecode(aws_iam_role_policy.irsa.policy).Statement : declaracao.Resource
        if anytrue([for acao in declaracao.Action : startswith(acao, "dynamodb:")])
      ]), "arn:aws:dynamodb:us-east-1:198903849859:${tabela}")
    ])
    error_message = "A política IRSA do OS precisa alcançar as duas tabelas da Saga."
  }

  assert {
    condition = alltrue([
      for declaracao in jsondecode(aws_iam_role_policy.irsa.policy).Statement :
      !anytrue([for acao in declaracao.Action : acao == "dynamodb:*" || acao == "*"])
    ])
    error_message = "A política IRSA do OS não pode usar curinga de ações do DynamoDB."
  }
}
