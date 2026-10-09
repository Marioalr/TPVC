#!/usr/bin/env python3
"""Compara dos carpetas de capturas de la prueba de humo (artefacto ``capturas-interfaz`` de la CI).

Fase 6: cada grupo de la separación de MedicalRecordsViewModel debe dejar la interfaz idéntica a la
base (paso 0). Se comparan los PNG con el mismo nombre, pixel por pixel, tapando antes las zonas que
cambian solas con la fecha y la hora (reloj, fecha de hoy, hora de guardado...). Cualquier diferencia
que quede se debe explicar.

Uso:
    python3 scripts/fase6/compare_captures.py BASE NUEVA [--masks scripts/fase6/capture-masks.json]
        [--mask x,y,ancho,alto] [--tolerance 8] [--diff-dir carpeta]

Las máscaras se leen de un JSON {"patrón de archivo": [[x, y, ancho, alto], ...]}; el patrón usa
comodines (fnmatch) sobre el nombre del archivo, y "*" aplica a todos. --mask agrega una zona para
todas las capturas. Sale con 1 si hay archivos distintos o que faltan; con 0 si todo coincide.

Requiere Pillow (pip install pillow).
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import os
import sys
from typing import Dict, List, Tuple

try:
    from PIL import Image, ImageChops, ImageDraw
except ImportError:  # pragma: no cover - mensaje claro si falta Pillow
    sys.stderr.write("Falta Pillow: pip install pillow\n")
    sys.exit(2)

Rect = Tuple[int, int, int, int]

DEFAULT_MASKS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "capture-masks.json")


def parse_rect(text: str) -> Rect:
    parts = [int(p) for p in text.split(",")]
    if len(parts) != 4:
        raise argparse.ArgumentTypeError(f"Zona inválida '{text}': se espera x,y,ancho,alto")
    return parts[0], parts[1], parts[2], parts[3]


def load_masks(path: str | None) -> Dict[str, List[Rect]]:
    if not path:
        return {}
    if not os.path.exists(path):
        if path == DEFAULT_MASKS:
            return {}
        raise SystemExit(f"No existe el archivo de máscaras {path}")
    with open(path, encoding="utf-8") as handle:
        raw = json.load(handle)
    masks: Dict[str, List[Rect]] = {}
    for pattern, rects in raw.items():
        if pattern.startswith("_"):
            continue  # comentarios
        masks[pattern] = [tuple(int(v) for v in rect) for rect in rects]  # type: ignore[misc]
    return masks


def masks_for(name: str, masks: Dict[str, List[Rect]], extra: List[Rect]) -> List[Rect]:
    rects = list(extra)
    for pattern, pattern_rects in masks.items():
        if fnmatch.fnmatch(name, pattern):
            rects.extend(pattern_rects)
    return rects


def apply_masks(image: Image.Image, rects: List[Rect]) -> Image.Image:
    if not rects:
        return image
    masked = image.copy()
    draw = ImageDraw.Draw(masked)
    for x, y, w, h in rects:
        draw.rectangle([x, y, x + w - 1, y + h - 1], fill=(255, 0, 255, 255))
    return masked


def compare(base_path: str, new_path: str, rects: List[Rect], tolerance: int, diff_dir: str | None) -> Tuple[int, Tuple[int, int, int, int] | None, str | None]:
    """Devuelve (pixeles distintos, recuadro que los contiene, error)."""
    with Image.open(base_path) as base_image, Image.open(new_path) as new_image:
        base = base_image.convert("RGBA")
        new = new_image.convert("RGBA")
    if base.size != new.size:
        return -1, None, f"tamaño distinto: {base.size} contra {new.size}"

    base = apply_masks(base, rects)
    new = apply_masks(new, rects)
    diff = ImageChops.difference(base, new)
    # Un pixel cuenta como distinto si algún canal cambia más que la tolerancia (suavizado de texto)
    channels = diff.split()
    mask = channels[0].point(lambda v: 255 if v > tolerance else 0)
    for channel in channels[1:]:
        mask = ImageChops.lighter(mask, channel.point(lambda v: 255 if v > tolerance else 0))
    histogram = mask.histogram()
    changed = histogram[255]
    box = mask.getbbox() if changed else None

    if changed and diff_dir:
        os.makedirs(diff_dir, exist_ok=True)
        highlight = new.convert("RGB")
        overlay = Image.new("RGB", highlight.size, (255, 0, 0))
        highlight = Image.composite(overlay, highlight, mask)
        highlight.save(os.path.join(diff_dir, os.path.basename(new_path)))
    return changed, box, None


def main(argv: List[str]) -> int:
    parser = argparse.ArgumentParser(description="Compara dos carpetas de capturas PNG tapando la fecha y la hora.")
    parser.add_argument("base", help="Carpeta de capturas de referencia (paso 0)")
    parser.add_argument("new", help="Carpeta de capturas a revisar")
    parser.add_argument("--masks", default=DEFAULT_MASKS, help="JSON con zonas a tapar por archivo (por defecto capture-masks.json junto al script)")
    parser.add_argument("--mask", action="append", type=parse_rect, default=[], help="Zona x,y,ancho,alto a tapar en todas las capturas (se puede repetir)")
    parser.add_argument("--tolerance", type=int, default=8, help="Diferencia por canal que se ignora (0-255, por defecto 8)")
    parser.add_argument("--diff-dir", help="Guarda aquí cada captura distinta con los pixeles cambiados en rojo")
    args = parser.parse_args(argv)

    for folder in (args.base, args.new):
        if not os.path.isdir(folder):
            parser.error(f"No existe la carpeta {folder}")

    masks = load_masks(args.masks)
    base_files = {f for f in os.listdir(args.base) if f.lower().endswith(".png")}
    new_files = {f for f in os.listdir(args.new) if f.lower().endswith(".png")}

    problems = 0
    for name in sorted(base_files - new_files):
        print(f"FALTA     {name} (está en la base y no en la nueva)")
        problems += 1
    for name in sorted(new_files - base_files):
        print(f"NUEVA     {name} (no está en la base)")
        problems += 1

    same = 0
    for name in sorted(base_files & new_files):
        changed, box, error = compare(os.path.join(args.base, name), os.path.join(args.new, name),
                                      masks_for(name, masks, args.mask), args.tolerance, args.diff_dir)
        if error:
            print(f"DISTINTA  {name}: {error}")
            problems += 1
        elif changed:
            x0, y0, x1, y1 = box  # type: ignore[misc]
            print(f"DISTINTA  {name}: {changed} pixeles en x={x0}..{x1 - 1}, y={y0}..{y1 - 1}")
            problems += 1
        else:
            same += 1

    print(f"\n{same} iguales, {problems} con diferencias o faltantes ({len(base_files | new_files)} capturas)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
