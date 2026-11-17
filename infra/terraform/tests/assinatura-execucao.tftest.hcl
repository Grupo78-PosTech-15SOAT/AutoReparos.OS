# C20 de infra-ci-observabilidade (D-67): a fila autoreparos-os-saga assina o tópico da Execução com raw delivery,
# filtro por `tipo` e política de fila que só aceita o SNS vindo desse tópico.

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

run "assinatura_do_topico_da_execucao" {
  command = apply

  assert {
    condition = (
      aws_sns_topic_subscription.saga_eventos_execucao.topic_arn == "arn:aws:sns:us-east-1:198903849859:autoreparos-execucao-eventos" &&
      aws_sns_topic_subscription.saga_eventos_execucao.protocol == "sqs" &&
      aws_sns_topic_subscription.saga_eventos_execucao.endpoint == aws_sqs_queue.saga.arn
    )
    error_message = "A fila autoreparos-os-saga precisa assinar o tópico autoreparos-execucao-eventos pelo protocolo sqs."
  }

  assert {
    condition     = aws_sns_topic_subscription.saga_eventos_execucao.raw_message_delivery == true
    error_message = "A assinatura precisa de raw delivery: os consumidores leem o envelope publicado sem o invólucro do SNS."
  }

  assert {
    condition = (
      aws_sns_topic_subscription.saga_eventos_execucao.filter_policy_scope == "MessageAttributes" &&
      jsondecode(aws_sns_topic_subscription.saga_eventos_execucao.filter_policy) == { tipo = ["execucao.concluida.v1", "execucao.falhou.v1", "execucao.liberacao-recusada.v1", "execucao.cancelada.v1", "execucao.cancelamento-recusado.v1", "insumos.reservados.v1", "insumos.reserva-falhou.v1", "insumos.reserva-cancelada.v1"] }
    )
    error_message = "O filtro da assinatura precisa ser exatamente os tipos que a fila consome, pelo atributo tipo."
  }

  assert {
    condition = (
      jsondecode(aws_sqs_queue_policy.saga_recebe_do_topico_execucao.policy).Statement[0].Principal.Service == "sns.amazonaws.com" &&
      jsondecode(aws_sqs_queue_policy.saga_recebe_do_topico_execucao.policy).Statement[0].Action == "sqs:SendMessage" &&
      jsondecode(aws_sqs_queue_policy.saga_recebe_do_topico_execucao.policy).Statement[0].Condition.ArnEquals["aws:SourceArn"] == "arn:aws:sns:us-east-1:198903849859:autoreparos-execucao-eventos" &&
      length(jsondecode(aws_sqs_queue_policy.saga_recebe_do_topico_execucao.policy).Statement) == 1
    )
    error_message = "A política da fila só pode aceitar sqs:SendMessage do SNS vindo do tópico da Execução."
  }
}
