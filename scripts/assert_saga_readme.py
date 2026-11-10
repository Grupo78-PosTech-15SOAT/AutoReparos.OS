#!/usr/bin/env python3
"""Verifica que o README do OS documenta a Saga: secao `## Saga`, estrategia, armazenamento de estado e a
justificativa contra coreografia.

Uso (a partir de qualquer diretorio; o README e localizado relativo a este arquivo):

    python scripts/assert_saga_readme.py --name saga-rationale --strategy orchestrated --state-store DynamoDB

Saidas: 0 = tudo presente; 1 = falta alguma parte (mensagem clara em stderr); 2 = erro de uso ou README ausente.
"""
import argparse
import re
import sys
import unicodedata
from pathlib import Path

ESTRATEGIAS = {
    "orchestrated": ("orquestrad", "orchestrat"),
    "choreographed": ("coreografad", "choreograph"),
}
TERMOS_JUSTIFICATIVA = ("visibilidade", "retomada", "compensacao ordenada", "auditoria")
MINIMO_TERMOS_JUSTIFICATIVA = 2


def normalizar(texto):
    sem_acento = unicodedata.normalize("NFKD", texto).encode("ascii", "ignore").decode("ascii")
    return sem_acento.lower()


def extrair_secao_saga(markdown):
    """Devolve o texto da secao `## Saga` (ate o proximo titulo de nivel 2) ou None."""
    linhas = markdown.splitlines()
    inicio = None
    for i, linha in enumerate(linhas):
        if re.fullmatch(r"##\s+Saga\s*", linha):
            inicio = i
            break
    if inicio is None:
        return None
    fim = len(linhas)
    for j in range(inicio + 1, len(linhas)):
        if re.match(r"##\s+(?!#)", linhas[j]):
            fim = j
            break
    return "\n".join(linhas[inicio:fim])


def verificar(markdown, estrategia, armazenamento):
    """Devolve a lista de problemas encontrados (vazia quando o README esta conforme)."""
    secao = extrair_secao_saga(markdown)
    if secao is None:
        return ["secao `## Saga` ausente no README"]
    problemas = []
    texto = normalizar(secao)
    termos = ESTRATEGIAS[estrategia]
    if not any(t in texto for t in termos):
        problemas.append(f"a secao `## Saga` nao descreve a estrategia '{estrategia}' (esperado um dos termos: {', '.join(termos)})")
    if normalizar(armazenamento) not in texto:
        problemas.append(f"a secao `## Saga` nao cita o armazenamento de estado '{armazenamento}'")
    if "coreografia" not in texto:
        problemas.append("a secao `## Saga` nao compara a estrategia com coreografia (termo 'coreografia' ausente)")
    else:
        presentes = [t for t in TERMOS_JUSTIFICATIVA if t in texto]
        if len(presentes) < MINIMO_TERMOS_JUSTIFICATIVA:
            problemas.append(
                "a justificativa contra coreografia e insuficiente: cite ao menos "
                f"{MINIMO_TERMOS_JUSTIFICATIVA} de [{', '.join(TERMOS_JUSTIFICATIVA)}] (encontrados: {', '.join(presentes) or 'nenhum'})"
            )
    return problemas


def main(argv=None):
    parser = argparse.ArgumentParser(description="Valida a documentacao da Saga no README do OS.")
    parser.add_argument("--name", required=True, help="nome da verificacao (apenas para o relatorio)")
    parser.add_argument("--strategy", required=True, choices=sorted(ESTRATEGIAS), help="estrategia que deve estar documentada")
    parser.add_argument("--state-store", required=True, help="armazenamento de estado que deve estar citado (ex.: DynamoDB)")
    parser.add_argument("--readme", help="caminho do README (padrao: README.md ao lado da pasta scripts)")
    args = parser.parse_args(argv)

    readme = Path(args.readme) if args.readme else Path(__file__).resolve().parent.parent / "README.md"
    if not readme.is_file():
        print(f"[{args.name}] FALHA: README nao encontrado em {readme}", file=sys.stderr)
        return 2
    problemas = verificar(readme.read_text(encoding="utf-8"), args.strategy, args.state_store)
    if problemas:
        for problema in problemas:
            print(f"[{args.name}] FALHA: {problema}", file=sys.stderr)
        return 1
    print(f"[{args.name}] OK: README documenta a estrategia {args.strategy} com estado em {args.state_store} e justificativa contra coreografia ({readme})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
