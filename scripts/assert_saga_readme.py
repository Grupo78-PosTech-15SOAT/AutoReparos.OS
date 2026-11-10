#!/usr/bin/env python3
"""Verifica que o README do OS documenta a Saga: secao `## Saga`, estrategia, armazenamento de estado e a
justificativa contra coreografia.

A justificativa precisa existir de fato, nao apenas como palavras-chave: dentro da secao `## Saga` deve haver um
subtitulo de justificativa (que mencione coreografia) cujo corpo -- o texto abaixo do subtitulo, sem contar o
proprio titulo -- tenha pelo menos 40 palavras, compare orquestracao e coreografia e cite pelo menos 3 dos 4 criterios
(visibilidade, retomada, compensacao, auditoria).

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
# Cada criterio aceita as formas usadas no texto (apos normalizar acentos e caixa).
CRITERIOS_JUSTIFICATIVA = {
    "visibilidade": ("visibilidade",),
    "retomada": ("retomada", "retomar"),
    "compensacao": ("compensacao", "compensar"),
    "auditoria": ("auditoria", "auditar"),
}
MINIMO_CRITERIOS = 3
MINIMO_PALAVRAS_JUSTIFICATIVA = 40
TERMOS_COREOGRAFIA = ("coreografia", "coreografad")
TERMOS_ORQUESTRACAO = ("orquestracao", "orquestrad", "orquestrador")


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


def subsecoes(secao):
    """Divide a secao em pares (titulo, corpo) por subtitulos de nivel 3 ou mais (`###`...). O texto antes do primeiro
    subtitulo nao pertence a nenhuma subsecao. O corpo vai ate o proximo subtitulo de qualquer nivel de 3 em diante."""
    pares = []
    titulo = None
    corpo = []
    for linha in secao.splitlines():
        if re.match(r"#{3,}\s+\S", linha):
            if titulo is not None:
                pares.append((titulo, "\n".join(corpo)))
            titulo = re.sub(r"^#{3,}\s+", "", linha).strip()
            corpo = []
        elif titulo is not None:
            corpo.append(linha)
    if titulo is not None:
        pares.append((titulo, "\n".join(corpo)))
    return pares


def contar_palavras(texto):
    # Ignora cercas de codigo e marcadores de lista; conta apenas palavras de verdade.
    sem_codigo = re.sub(r"```.*?```", " ", texto, flags=re.DOTALL)
    return len(re.findall(r"[A-Za-zÀ-ÿ]{2,}", sem_codigo))


def avaliar_justificativa(titulo, corpo):
    """Devolve (problemas, criterios_citados) para uma subsecao candidata."""
    problemas = []
    texto = normalizar(corpo)
    palavras = contar_palavras(corpo)
    if palavras < MINIMO_PALAVRAS_JUSTIFICATIVA:
        problemas.append(f"o corpo tem {palavras} palavras (minimo {MINIMO_PALAVRAS_JUSTIFICATIVA}); o titulo sozinho nao conta")
    if not any(t in texto for t in TERMOS_COREOGRAFIA):
        problemas.append("o corpo nao menciona coreografia")
    if not any(t in texto for t in TERMOS_ORQUESTRACAO):
        problemas.append("o corpo nao menciona orquestracao (a comparacao precisa citar as duas estrategias)")
    criterios = [nome for nome, formas in CRITERIOS_JUSTIFICATIVA.items() if any(f in texto for f in formas)]
    if len(criterios) < MINIMO_CRITERIOS:
        problemas.append(
            f"o corpo cita {len(criterios)} criterios ({', '.join(criterios) or 'nenhum'}); "
            f"minimo {MINIMO_CRITERIOS} de [{', '.join(CRITERIOS_JUSTIFICATIVA)}]"
        )
    return problemas, criterios


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

    candidatas = [(t, c) for t, c in subsecoes(secao) if any(termo in normalizar(t) for termo in TERMOS_COREOGRAFIA)]
    if not candidatas:
        problemas.append(
            "a secao `## Saga` nao tem um subtitulo de justificativa contra coreografia "
            "(esperado um subtitulo `###` cujo titulo mencione coreografia)"
        )
        return problemas

    falhas_por_candidata = []
    for titulo, corpo in candidatas:
        falhas, _ = avaliar_justificativa(titulo, corpo)
        if not falhas:
            return problemas
        falhas_por_candidata.append((titulo, falhas))
    titulo, falhas = falhas_por_candidata[0]
    problemas.append(f"a justificativa contra coreografia ('{titulo}') e insuficiente: " + "; ".join(falhas))
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
