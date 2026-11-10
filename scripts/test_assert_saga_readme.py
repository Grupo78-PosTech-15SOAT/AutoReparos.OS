"""Teste do verificador do README da Saga: casos positivos e negativos.

A justificativa precisa estar no corpo de um subtitulo (nao so no titulo): 40 palavras ou mais, comparando orquestracao
e coreografia e citando ao menos 3 de 4 criterios (visibilidade, retomada, compensacao, auditoria).

Executar: python -m unittest discover -s scripts -p "test_*.py"
"""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent / "assert_saga_readme.py"
README_REAL = SCRIPT.parent.parent / "README.md"

ABERTURA = "A Saga e **orquestrada**: o OS Service decide cada passo. O estado fica em DynamoDB."

# 3 criterios (visibilidade, retomada, auditoria), 52 palavras, cita orquestracao e coreografia.
CORPO_OK = (
    "Na coreografia cada servico reage aos eventos dos outros e ninguem conhece o fluxo inteiro, enquanto na "
    "orquestracao um coordenador conhece o fluxo e guarda o estado. A visibilidade do andamento fica central, a "
    "retomada depois de uma queda parte do ultimo passo persistido e a auditoria de cada transicao fica registrada "
    "na propria Saga, o que a coreografia nao entrega."
)


def montar_readme(titulo="### Por que orquestracao e nao coreografia", corpo=CORPO_OK, abertura=ABERTURA, depois=""):
    partes = ["# Servico", "", "## Saga", "", abertura, ""]
    if titulo is not None:
        partes += [titulo, "", corpo, ""]
    partes += [depois, "## Outra secao", ""]
    return "\n".join(partes)


def palavras(quantidade):
    """Corpo com exatamente `quantidade` palavras, que cita orquestracao, coreografia e os criterios necessarios."""
    base = "coreografia orquestracao visibilidade retomada auditoria".split()
    extras = ["texto"] * max(0, quantidade - len(base))
    return " ".join((base + extras)[:quantidade])


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
    def test_corpo_de_exemplo_tem_pelo_menos_40_palavras(self):
        self.assertGreaterEqual(len(CORPO_OK.split()), 40)

    def test_readme_completo_passa(self):
        r = executar(montar_readme())
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("[saga-rationale] OK", r.stdout)

    def test_readme_real_do_os_passa(self):
        r = subprocess.run(
            [sys.executable, str(SCRIPT), "--name", "saga-rationale", "--strategy", "orchestrated",
             "--state-store", "DynamoDB", "--readme", str(README_REAL)],
            capture_output=True, text=True, encoding="utf-8",
        )
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_sem_secao_saga_falha(self):
        r = executar(montar_readme().replace("## Saga", "## Outro titulo"))
        self.assertEqual(r.returncode, 1)
        self.assertIn("secao `## Saga` ausente", r.stderr)

    def test_sem_estrategia_orquestrada_falha(self):
        r = executar(montar_readme(abertura="A Saga e centralizada. O estado fica em DynamoDB.").replace("orquestracao", "centralizacao").replace("orquestrada", "centralizada"))
        self.assertEqual(r.returncode, 1)
        self.assertIn("estrategia 'orchestrated'", r.stderr)

    def test_sem_armazenamento_dynamodb_falha(self):
        r = executar(montar_readme(abertura="A Saga e **orquestrada**. O estado fica em um banco."))
        self.assertEqual(r.returncode, 1)
        self.assertIn("armazenamento de estado 'DynamoDB'", r.stderr)

    def test_titulo_de_justificativa_sem_corpo_falha(self):
        # O titulo sozinho cita coreografia, mas nao ha justificativa: o texto de abertura cita os criterios fora do subtitulo.
        abertura = ABERTURA + " Visibilidade, retomada, compensacao e auditoria importam; a coreografia nao entrega isso."
        r = executar(montar_readme(abertura=abertura, corpo=""))
        self.assertEqual(r.returncode, 1)
        self.assertIn("justificativa contra coreografia ('Por que orquestracao e nao coreografia') e insuficiente", r.stderr)
        self.assertIn("o corpo tem 0 palavras (minimo 40)", r.stderr)

    def test_titulo_sem_corpo_seguido_de_outro_subtitulo_falha(self):
        r = executar(montar_readme(depois="### Diagrama\n\n" + CORPO_OK + "\n\n"))
        self.assertEqual(r.returncode, 0, "o corpo da justificativa continua valendo")
        r = executar(montar_readme(corpo="", depois="### Diagrama\n\n" + CORPO_OK + "\n\n"))
        self.assertEqual(r.returncode, 1, "texto de outro subtitulo nao conta como corpo da justificativa")
        self.assertIn("o corpo tem 0 palavras", r.stderr)

    def test_sem_subtitulo_de_justificativa_falha(self):
        r = executar(montar_readme(titulo=None, abertura=ABERTURA + " " + CORPO_OK))
        self.assertEqual(r.returncode, 1)
        self.assertIn("nao tem um subtitulo de justificativa contra coreografia", r.stderr)

    def test_subtitulo_sem_mencionar_coreografia_no_titulo_nao_conta(self):
        r = executar(montar_readme(titulo="### Justificativa"))
        self.assertEqual(r.returncode, 1)
        self.assertIn("nao tem um subtitulo de justificativa contra coreografia", r.stderr)

    def test_corpo_curto_falha(self):
        r = executar(montar_readme(corpo=palavras(39)))
        self.assertEqual(r.returncode, 1)
        self.assertIn("o corpo tem 39 palavras (minimo 40)", r.stderr)

    def test_corpo_com_exatamente_40_palavras_passa(self):
        r = executar(montar_readme(corpo=palavras(40)))
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_com_apenas_um_criterio_falha(self):
        corpo = CORPO_OK.replace("auditoria", "registro").replace("retomada", "continuidade")
        r = executar(montar_readme(corpo=corpo))
        self.assertEqual(r.returncode, 1)
        self.assertIn("o corpo cita 1 criterios (visibilidade)", r.stderr)

    def test_com_tres_criterios_passa_e_com_dois_falha(self):
        dois_criterios = CORPO_OK.replace("auditoria", "registro")  # visibilidade + retomada: falha
        self.assertEqual(executar(montar_readme(corpo=dois_criterios)).returncode, 1)
        tres_criterios = dois_criterios.replace("registro", "compensacao")  # visibilidade + retomada + compensacao: passa
        self.assertEqual(executar(montar_readme(corpo=tres_criterios)).returncode, 0)
        dois = CORPO_OK.replace("auditoria", "registro").replace("retomada", "continuidade").replace("visibilidade", "clareza")
        self.assertEqual(executar(montar_readme(corpo=dois)).returncode, 1)

    def test_corpo_sem_mencionar_coreografia_falha(self):
        corpo = CORPO_OK.replace("coreografia", "reacao em cadeia")
        r = executar(montar_readme(corpo=corpo))
        self.assertEqual(r.returncode, 1)
        self.assertIn("o corpo nao menciona coreografia", r.stderr)

    def test_corpo_sem_mencionar_orquestracao_falha(self):
        corpo = CORPO_OK.replace("orquestracao", "controle central")
        r = executar(montar_readme(corpo=corpo))
        self.assertEqual(r.returncode, 1)
        self.assertIn("o corpo nao menciona orquestracao", r.stderr)

    def test_conteudo_fora_da_secao_saga_nao_conta(self):
        r = executar("# Servico\n\n## Saga\n\nTexto sem nada util.\n\n## Detalhes\n\n### Por que orquestracao e nao coreografia\n\n"
                     + CORPO_OK + "\n\norquestrada DynamoDB\n")
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
