r"""Модели запасного OCR (PaddleOCR PP-OCRv4 в формате ONNX) -> data/models/ppocr/.

Берутся из пакета rapidocr-onnxruntime (Apache-2.0): детектор текста и распознаватель строк. Список символов
распознавателя лежит внутри его onnx-файла (метаданные «character»), отдельного словаря не нужно.
Классификатор поворота не нужен: экранный текст не перевёрнут.

    .venv\Scripts\python.exe export_ppocr.py
"""
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CACHE = Path(__file__).resolve().parent / ".cache" / "ppocr"
OUT = ROOT / "data" / "models" / "ppocr"
PACKAGE = "rapidocr-onnxruntime==1.4.4"
FILES = {
    "ch_PP-OCRv4_det_infer.onnx": "det.onnx",
    "ch_PP-OCRv4_rec_infer.onnx": "rec.onnx",
}


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    CACHE.mkdir(parents=True, exist_ok=True)
    OUT.mkdir(parents=True, exist_ok=True)
    wheels = list(CACHE.glob("rapidocr_onnxruntime-*.whl"))
    if not wheels:
        subprocess.run([sys.executable, "-m", "pip", "download", PACKAGE, "--no-deps", "-d", str(CACHE)], check=True)
        wheels = list(CACHE.glob("rapidocr_onnxruntime-*.whl"))
    with zipfile.ZipFile(wheels[0]) as wheel:
        for source, target in FILES.items():
            with wheel.open(f"rapidocr_onnxruntime/models/{source}") as src, open(OUT / target, "wb") as dst:
                shutil.copyfileobj(src, dst)
            print(f"{OUT / target}  {(OUT / target).stat().st_size / 1e6:.1f} МБ")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
