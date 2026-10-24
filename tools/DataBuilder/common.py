"""Общие пути и утилиты для скриптов подготовки данных."""

from __future__ import annotations

import logging
import os
import sys
import time
import urllib.request
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
REPO_ROOT = TOOLS_DIR.parent.parent
CACHE_DIR = TOOLS_DIR / ".cache"
DATA_DIR = REPO_ROOT / "data"
MODELS_DIR = DATA_DIR / "models"

# Кэш HuggingFace держим внутри .cache, чтобы всё скачанное лежало в одном месте.
os.environ.setdefault("HF_HOME", str(CACHE_DIR / "hf"))


def setup_logging() -> logging.Logger:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
        datefmt="%H:%M:%S",
        stream=sys.stdout,
    )
    return logging.getLogger("databuilder")


def download(url: str, dest: Path, log: logging.Logger, attempts: int = 50) -> Path:
    """Скачивает файл с докачкой и повторами: kaikki.org периодически рвёт соединение."""
    for attempt in range(1, attempts + 1):
        try:
            return _download_once(url, dest, log)
        except (OSError, RuntimeError) as e:  # URLError, ConnectionResetError, таймауты
            if attempt == attempts:
                raise
            log.warning("Обрыв загрузки (%s), попытка %d/%d через 5 с", e, attempt + 1, attempts)
            time.sleep(5)
    raise AssertionError("unreachable")


def _download_once(url: str, dest: Path, log: logging.Logger) -> Path:
    dest.parent.mkdir(parents=True, exist_ok=True)
    head = urllib.request.Request(url, method="HEAD")
    with urllib.request.urlopen(head, timeout=60) as resp:
        total = int(resp.headers.get("Content-Length", 0))

    if dest.exists() and (total == 0 or dest.stat().st_size == total):
        log.info("Уже скачано: %s", dest.name)
        return dest

    part = dest.with_name(dest.name + ".part")
    done = part.stat().st_size if part.exists() else 0
    req = urllib.request.Request(url)
    if done:
        req.add_header("Range", f"bytes={done}-")
        log.info("Докачка %s с %.1f МБ", dest.name, done / 2**20)
    else:
        log.info("Скачивание %s (%.1f МБ)", url, total / 2**20)

    last = time.monotonic()
    with urllib.request.urlopen(req, timeout=60) as resp, open(part, "ab") as out:
        if done and resp.status != 206:
            # Сервер не поддержал Range — начинаем заново.
            out.truncate(0)
            done = 0
        while chunk := resp.read(1 << 20):
            out.write(chunk)
            done += len(chunk)
            if time.monotonic() - last > 10:
                last = time.monotonic()
                log.info("  %.0f / %.0f МБ", done / 2**20, total / 2**20)

    if total and part.stat().st_size != total:
        raise RuntimeError(f"Размер {part} не совпадает с ожидаемым {total}")
    part.replace(dest)
    return dest


def human_size(path: Path) -> str:
    size = path.stat().st_size if path.is_file() else sum(
        p.stat().st_size for p in path.rglob("*") if p.is_file()
    )
    return f"{size / 2**20:.1f} МБ"
