"""Teste do verificador do README da Saga: casos positivo e negativos.

Executar: python -m unittest discover -s scripts -p "test_*.py"
"""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent / "assert_saga_readme.py"

README_OK = """# Servico

## Saga

A Saga e **orquestrada**: o OS Service decide cada passo. O estado fica em DynamoDB.

### Por que nao coreografia

Na coreografia nao ha visibilidade central do fluxo nem retomada simples depois de uma queda.

## Outra secao
"""


def executar(readme_texto):
    with tempfile.TemporaryDirectory() as pasta:
        caminho = Path(pasta) / "README.md"
        caminho.write_text(readme_texto, encoding="utf-8")
        return subprocess.run(
            [sys.executable, str(SCRIPT), "--name", "saga-rationale", "--strategy", "orchestrated",
             "--state-store", "DynamoDB", "--readme", str(caminho)],
            capture_output=True, text=True, encoding="utf-8",
        )


class AssertSagaReadmeTests(unittest.TestCase):
    def test_readme_completo_passa(self):
        r = executar(README_OK)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("[saga-rationale] OK", r.stdout)

    def test_sem_secao_saga_falha(self):
        r = executar(README_OK.replace("## Saga", "## Outro titulo"))
        self.assertEqual(r.returncode, 1)
        self.assertIn("secao `## Saga` ausente", r.stderr)

    def test_sem_estrategia_orquestrada_falha(self):
        r = executar(README_OK.replace("orquestrada", "centralizada"))
        self.assertEqual(r.returncode, 1)
        self.assertIn("estrategia 'orchestrated'", r.stderr)

    def test_sem_armazenamento_dynamodb_falha(self):
        r = executar(README_OK.replace("DynamoDB", "um banco"))
        self.assertEqual(r.returncode, 1)
        self.assertIn("armazenamento de estado 'DynamoDB'", r.stderr)

    def test_sem_comparacao_com_coreografia_falha(self):
        r = executar(README_OK.replace("coreografia", "outro modelo"))
        self.assertEqual(r.returncode, 1)
        self.assertIn("termo 'coreografia' ausente", r.stderr)

    def test_justificativa_fraca_falha(self):
        r = executar(README_OK.replace("visibilidade", "rapidez").replace("retomada", "simplicidade"))
        self.assertEqual(r.returncode, 1)
        self.assertIn("justificativa contra coreografia e insuficiente", r.stderr)

    def test_conteudo_fora_da_secao_saga_nao_conta(self):
        r = executar("# Servico\n\n## Saga\n\nTexto sem nada util.\n\n## Detalhes\n\n"
                     "orquestrada DynamoDB coreografia visibilidade retomada\n")
        self.assertEqual(r.returncode, 1)

    def test_readme_inexistente_sai_com_codigo_2(self):
        inexistente = Path(tempfile.gettempdir()) / "nao-existe-saga" / "README.md"
        r = subprocess.run(
            [sys.executable, str(SCRIPT), "--name", "x", "--strategy", "orchestrated",
             "--state-store", "DynamoDB", "--readme", str(inexistente)],
            capture_output=True, text=True, encoding="utf-8",
        )
        self.assertEqual(r.returncode, 2)
        self.assertIn("README nao encontrado", r.stderr)


if __name__ == "__main__":
    unittest.main()
