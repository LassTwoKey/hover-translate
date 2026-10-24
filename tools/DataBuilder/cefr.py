"""Уровни CEFR из Open Language Profiles (CEFR-J A1–B2 + Octanove C1/C2)."""

from __future__ import annotations

import csv
import logging
from pathlib import Path

from common import CACHE_DIR, download

BASE_URL = "https://raw.githubusercontent.com/openlanguageprofiles/olp-en-cefrj/master/"
FILES = ("cefrj-vocabulary-profile-1.5.csv", "octanove-vocabulary-profile-c1c2-1.0.csv")
LEVELS = ("A1", "A2", "B1", "B2", "C1", "C2")


def load_cefr(log: logging.Logger) -> dict[str, str]:
    """Возвращает {слово в нижнем регистре: минимальный уровень}.

    Уровень привязан к слову, а не к части речи: в DictEntry один бейдж CEFR на лемму,
    поэтому берём самый ранний уровень (например, «light» noun A1 / verb B1 -> A1).
    """
    levels: dict[str, str] = {}
    for name in FILES:
        path: Path = download(BASE_URL + name, CACHE_DIR / name, log)
        with open(path, encoding="utf-8", newline="") as f:
            for row in csv.DictReader(f):
                level = (row.get("CEFR") or "").strip().upper()
                if level not in LEVELS:
                    continue
                # «a.m./A.M./am/AM», «colour/color» — варианты написания через слэш.
                for variant in row["headword"].split("/"):
                    word = variant.strip().lower().replace("’", "'")
                    if not word:
                        continue
                    old = levels.get(word)
                    if old is None or LEVELS.index(level) < LEVELS.index(old):
                        levels[word] = level
    log.info("CEFR: %d слов", len(levels))
    return levels
