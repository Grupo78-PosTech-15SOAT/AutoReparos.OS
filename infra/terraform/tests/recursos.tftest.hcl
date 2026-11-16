# C11: ECR, filas de entrada com DLQ, role IRSA presa ao ServiceAccount os/os e segredo da database do OS.
# Roda sem AWS: provider aws simulado e parâmetros SSM da plataforma substituídos por valores de exemplo.

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

run "recursos_do_os" {
  command = apply

  assert {
    condition     = aws_ecr_repository.servico.name == "autoreparos-os"
    error_message = "O ECR do OS precisa se chamar autoreparos-os."
  }

  assert {
    condition     = aws_sqs_queue.saga.name == "autoreparos-os-saga" && aws_sqs_queue.saga_dlq.name == "autoreparos-os-saga-dlq"
    error_message = "A fila de entrada do OS é autoreparos-os-saga, com a DLQ autoreparos-os-saga-dlq."
  }

  assert {
    condition     = jsondecode(aws_sqs_queue.saga.redrive_policy).deadLetterTargetArn == aws_sqs_queue.saga_dlq.arn
    error_message = "A redrive_policy da fila da Saga precisa apontar para a DLQ da Saga."
  }

  assert {
    condition     = jsondecode(aws_sqs_queue.saga.redrive_policy).maxReceiveCount > 0
    error_message = "A redrive_policy da fila da Saga precisa de maxReceiveCount."
  }

  assert {
    condition     = aws_iam_role.irsa.name == "autoreparos-os-irsa"
    error_message = "A role IRSA do OS precisa se chamar autoreparos-os-irsa."
  }

  assert {
    condition     = aws_iam_role.irsa.permissions_boundary == "arn:aws:iam::198903849859:policy/autoreparos-servico-boundary"
    error_message = "A role IRSA precisa do permissions boundary autoreparos-servico-boundary."
  }

  assert {
    condition     = jsondecode(aws_iam_role.irsa.assume_role_policy).Statement[0].Principal.Federated == "arn:aws:iam::198903849859:oidc-provider/oidc.eks.us-east-1.amazonaws.com/id/EXEMPLO"
    error_message = "A trust da role IRSA precisa confiar no provedor OIDC do cluster lido do SSM."
  }

  assert {
    condition     = jsondecode(aws_iam_role.irsa.assume_role_policy).Statement[0].Condition.StringEquals["oidc.eks.us-east-1.amazonaws.com/id/EXEMPLO:sub"] == "system:serviceaccount:os:os"
    error_message = "A trust da role IRSA precisa ficar presa ao ServiceAccount os do namespace os."
  }

  assert {
    condition     = length(jsondecode(aws_iam_role.irsa.assume_role_policy).Statement) == 1
    error_message = "A trust da role IRSA não pode ter outras declarações."
  }

  assert {
    condition     = aws_secretsmanager_secret.database.name == "autoreparos/production/os/database"
    error_message = "O segredo da database do OS precisa se chamar autoreparos/production/os/database."
  }

  assert {
    condition = (
      jsondecode(aws_secretsmanager_secret_version.database.secret_string).username == "os_app" &&
      jsondecode(aws_secretsmanager_secret_version.database.secret_string).dbname == "os_db" &&
      jsondecode(aws_secretsmanager_secret_version.database.secret_string).host == "autoreparos-db.exemplo.us-east-1.rds.amazonaws.com" &&
      jsondecode(aws_secretsmanager_secret_version.database.secret_string).port == 5432
    )
    error_message = "O segredo precisa de username os_app, dbname os_db, host do SSM e porta 5432."
  }

  assert {
    condition = strcontains(
      jsondecode(aws_secretsmanager_secret_version.database.secret_string).connectionString,
      "Host=autoreparos-db.exemplo.us-east-1.rds.amazonaws.com;Port=5432;Database=os_db;Username=os_app;Password=${random_password.database.result};SSL Mode=Prefer;"
    )
    error_message = "A connectionString do segredo precisa usar o host, a database, o usuário e a senha do próprio segredo."
  }
}
