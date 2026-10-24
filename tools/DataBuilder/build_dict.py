"""Сборка data/dict.db из английского Викисловаря (wiktextract / kaikki.org) + CEFR + частотности.

Этапы:
  1. Скачивание дампа wiktextract (по умолчанию — зафиксированная версия с зеркала HuggingFace,
     см. README: kaikki.org у нас отдавал ~400 Б/с).
  2. Извлечение английских записей в компактный кэш .cache/en-slim-<версия>.jsonl
     (долго один раз; при повторных запусках сразу переходим к п. 3).
  3. Фильтрация, привязка русских переводов к значениям, таблица словоформ, CEFR, запись SQLite.

Запуск:  .venv\\Scripts\\python.exe build_dict.py [--source mirror|kaikki] [--min-zipf 2.0]
"""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import re
import sqlite3
import time
import unicodedata
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timezone
from functools import lru_cache
from pathlib import Path

from cefr import load_cefr
from common import CACHE_DIR, DATA_DIR, download, setup_logging

SCHEMA_VERSION = 1


@dataclass(frozen=True)
class Source:
    url: str
    filename: str
    sha256: str | None  # None — файл обновляется на сервере, проверить нечем
    version: str
    all_languages: bool  # True — сырой дамп со всеми языками, фильтруем lang_code == "en"


SOURCES = {
    # Тот же дамп, что kaikki.org опубликовал 2026-10-03 (метка в 2026-09-02.txt на зеркале).
    "mirror": Source(
        url="https://huggingface.co/datasets/DataDock/wiktextract/resolve/main/2026-09-02.jsonl.gz",
        filename="wiktextract-2026-09-02.jsonl.gz",
        sha256="3dac8a09e57827bef493e2e6b552fe917bf3b1fc55dee05c4e4a3702aff5dbdb",
        version="2026-09-02_2026-10-03_1a05e46_e3d6d4e",
        all_languages=True,
    ),
    "kaikki": Source(
        url="https://kaikki.org/dictionary/English/kaikki.org-dictionary-English.jsonl",
        filename="kaikki.org-dictionary-English.jsonl",
        sha256=None,
        version="kaikki-latest",
        all_languages=False,
    ),
}

# Части речи, которые пользователь не встретит как отдельное слово на экране.
SKIP_POS = {"prefix", "suffix", "infix", "interfix", "affix", "circumfix", "character",
            "symbol", "punct", "romanization", "combining_form"}
# Части речи, которые оставляем только при наличии русского перевода.
NEED_RU_POS = {"proverb"}
# Имена собственные без русского перевода оставляем только очень частотные («America»):
# у части имён переводы в Викисловаре вынесены на подстраницы и в дамп не попадают.
NAME_MIN_ZIPF = 4.0
# Значения с этими тегами отбрасываем, если у них нет русского перевода.
DROP_SENSE_TAGS = {"obsolete", "archaic"}
# Словоформы с этими тегами не попадают в таблицу форм.
SKIP_FORM_TAGS = {"table-tags", "inflection-template", "class", "obsolete", "archaic",
                  "romanization", "error-unknown-tag"}

MAX_SENSES = 20          # сверх этого оставляем только значения с русским переводом
MAX_EXAMPLES = 2
MAX_EXAMPLE_LEN = 220
MAX_QUOTE_LEN = 160      # цитаты длинные и часто архаичные — берём только короткие
MAX_TRANSLATIONS = 6
MAX_SYNONYMS = 10
MAX_DB_MB = 150

# Слово: латиница (вкл. Latin-1), цифры, пробел, апостроф, дефис, точка («a.m.»).
WORD_RE = re.compile(r"^[A-Za-z0-9À-ÖØ-öø-ÿ][A-Za-z0-9À-ÖØ-öø-ÿ '\-.]*$")
CYRILLIC_RE = re.compile(r"[А-Яа-яЁё]")
TRAILING_NOTE_RE = re.compile(r"\s+(?=[A-Za-z])[^А-Яа-яЁё]*$")
HOMOGLYPHS = str.maketrans("aeopcxyAEOPCXBKMHT", "аеорсхуАЕОРСХВКМНТ")
TOKEN_RE = re.compile(r"[a-z]+")
STOPWORDS = set("""a an the of to in on at by for with from as or and not no is are be been being
was were it its this that these those which who whom what into onto than then so such any some
one ones something someone someplace somebody used use especially often usually very""".split())

log = setup_logging()


# ---------------------------------------------------------------- извлечение

def ensure_source(src: Source) -> Path:
    path = download(src.url, CACHE_DIR / src.filename, log)
    if src.sha256:
        marker = path.with_name(path.name + ".sha256ok")
        if not marker.exists():
            log.info("Проверка SHA-256 %s", path.name)
            h = hashlib.sha256()
            with open(path, "rb") as f:
                while chunk := f.read(1 << 24):
                    h.update(chunk)
            if h.hexdigest() != src.sha256:
                raise RuntimeError(f"SHA-256 не совпадает: {h.hexdigest()} != {src.sha256}. Удалите файл и запустите снова.")
            marker.write_text(src.sha256)
    return path


def _clean(s: str) -> str:
    return s.replace("’", "'").strip()


def slim_record(r: dict) -> dict:
    """Оставляет только нужные поля — кэш в ~15 раз меньше исходных английских записей."""
    def ru(items: list[dict] | None) -> list[list]:
        return [[t["word"], t.get("sense")] for t in items or ()
                if t.get("lang_code") == "ru" and t.get("word")]

    senses = []
    for s in r.get("senses", ()):
        glosses = s.get("raw_glosses") or s.get("glosses") or []
        examples = []
        for e in s.get("examples", ()):
            text = (e.get("text") or "").strip()
            if text and (e.get("type") == "example" and len(text) <= MAX_EXAMPLE_LEN
                         or len(text) <= MAX_QUOTE_LEN):
                examples.append([text, e.get("type", "")])
        senses.append({
            "g": glosses[-1] if glosses else "",  # у подзначений glosses = [родитель, уточнение]
            "t": s.get("tags", []),
            "fo": [_clean(x["word"]) for x in (s.get("form_of") or []) + (s.get("alt_of") or []) if x.get("word")],
            "ex": examples,
            "syn": [x["word"] for x in s.get("synonyms", ()) if x.get("word")],
            "tr": ru(s.get("translations")),
        })
    return {
        "w": _clean(r["word"]),
        "p": r.get("pos", ""),
        "f": [[_clean(f["form"]), f.get("tags", [])] for f in r.get("forms", ()) if f.get("form")],
        "ipa": [[x["ipa"], x.get("tags", [])] for x in r.get("sounds", ()) if x.get("ipa")],
        "tr": ru(r.get("translations")),
        "syn": [x["word"] for x in r.get("synonyms", ()) if x.get("word")],
        "s": senses,
    }


def extract_english(src: Source, raw: Path) -> Path:
    slim = CACHE_DIR / f"en-slim-{src.version}.jsonl"
    if slim.exists() and slim.stat().st_mtime > raw.stat().st_mtime:
        log.info("Компактный кэш уже есть: %s", slim.name)
        return slim
    log.info("Извлечение английских записей из %s (однократно, 10–30 мин)…", raw.name)
    opener = gzip.open if raw.suffix == ".gz" else open
    t0, total, kept = time.monotonic(), 0, 0
    tmp = slim.with_suffix(".tmp")
    with opener(raw, "rb") as fin, open(tmp, "w", encoding="utf-8") as fout:
        for line in fin:
            total += 1
            # Быстрый отсев без разбора JSON: в сыром дампе ~90% записей на других языках.
            if src.all_languages and b'"lang_code": "en"' not in line:
                continue
            r = json.loads(line)
            if r.get("lang_code") != "en" or "word" not in r:
                continue
            fout.write(json.dumps(slim_record(r), ensure_ascii=False, separators=(",", ":")))
            fout.write("\n")
            kept += 1
            if total % 1_000_000 == 0:
                log.info("  %d млн строк, английских %d, %.0f с", total // 1_000_000, kept, time.monotonic() - t0)
    tmp.replace(slim)
    log.info("Английских записей: %d из %d", kept, total)
    return slim


# ---------------------------------------------------------------- сборка

def strip_stress(s: str) -> str:
    """Убирает знаки ударения (слова́рь -> словарь), сохраняя й и ё."""
    decomposed = unicodedata.normalize("NFD", s)
    return unicodedata.normalize("NFC", "".join(c for c in decomposed if c not in "\u0301\u0300"))


def content_words(s: str | None) -> set[str]:
    return {w for w in TOKEN_RE.findall((s or "").lower()) if len(w) > 2 and w not in STOPWORDS}


@dataclass
class Sense:
    pos: str
    gloss: str
    tags: list[str]
    examples: list[str]
    synonyms: list[str]
    tr: list[str] = field(default_factory=list)


@dataclass
class Lemma:
    word: str
    senses: list[Sense] = field(default_factory=list)
    ipa: list[tuple[str, list[str], int]] = field(default_factory=list)
    synonyms: list[str] = field(default_factory=list)

    @property
    def has_ru(self) -> bool:
        return any(s.tr for s in self.senses)


def fix_homoglyphs(s: str) -> str:
    """«Биaнка» (латинская a) -> «Бианка». Не трогает «3D-принтер», «CD-плеер»."""
    def fix(m: re.Match[str]) -> str:
        part = m.group(0)
        if CYRILLIC_RE.search(part) and all(ord(c) in HOMOGLYPHS for c in re.findall(r"[A-Za-z]", part)):
            return part.translate(HOMOGLYPHS)
        return part
    return re.sub(r"[^\s\-]+", fix, s)


def add_translation(sense: Sense, word: str) -> None:
    # «сдать pf trans.», «бросить pf the habit» — отрезаем латинские пометки после русского текста.
    w = fix_homoglyphs(TRAILING_NOTE_RE.sub("", strip_stress(word)).strip())
    if CYRILLIC_RE.search(w) and w not in sense.tr and len(sense.tr) < MAX_TRANSLATIONS:
        sense.tr.append(w)


def assign_translations(senses: list[Sense], groups: list[list]) -> None:
    """Привязывает переводы уровня слова (с подписью значения) к значениям.

    Подпись («not imprisoned») сравнивается со словами определения. Если подпись состоит
    из одних стоп-слов («not in use»), сравниваем по всем словам. Подпись, целиком входящая
    в определение, получает бонус. Устаревшие значения в кандидаты не берём. При нуле
    совпадений перевод уходит в первое актуальное значение без переводов — таблицы
    переводов в Викисловаре обычно идут в порядке значений.
    """
    candidates = [i for i, s in enumerate(senses) if not DROP_SENSE_TAGS & set(s.tags)]
    if not candidates:
        return
    glosses = [senses[i].gloss.lower() for i in candidates]
    content = [content_words(g) for g in glosses]
    every = [set(TOKEN_RE.findall(g)) for g in glosses]
    cache: dict[str | None, int] = {}
    for word, label in groups:
        if label not in cache:
            lw, pool = content_words(label), content
            if not lw:
                lw, pool = set(TOKEN_RE.findall((label or "").lower())), every
            best, best_score = -1, 0.0
            for k, gw in enumerate(pool):
                score = len(lw & gw) / len(lw) if lw else 0.0
                if label and label.lower() in glosses[k]:
                    score += 0.5
                if score > best_score:
                    best, best_score = k, score
            if best < 0:
                best = next((k for k, i in enumerate(candidates) if not senses[i].tr), 0)
            cache[label] = candidates[best]
        add_translation(senses[cache[label]], word)


def pick_ipa(cands: list[tuple[str, list[str], int]]) -> str | None:
    def rank(c: tuple[str, list[str], int]) -> tuple[int, int, int]:
        ipa, tags, order = c
        us = 0 if {"US", "General-American", "GenAm"} & set(tags) else (1 if not tags else 2)
        return us, 0 if ipa.startswith("/") else 1, order
    return min(cands, key=rank)[0] if cands else None


@lru_cache(maxsize=None)
def zipf_of(word: str) -> float:
    from wordfreq import zipf_frequency
    return zipf_frequency(word, "en")


def build(slim: Path, src: Source, min_zipf: float, min_phrase_zipf: float) -> None:
    from wordfreq import top_n_list

    lemmas: dict[str, Lemma] = {}
    forms_raw: list[tuple[str, str, str]] = []  # (форма, лемма, теги)
    ipa_order = 0
    stats: dict[str, int] = defaultdict(int)

    log.info("Разбор компактного кэша…")
    with open(slim, encoding="utf-8") as f:
        for line in f:
            r = json.loads(line)
            word, pos = r["w"], r["p"]
            stats["records"] += 1
            if not WORD_RE.match(word) or pos in SKIP_POS:
                stats["skip_word_or_pos"] += 1
                continue

            senses: list[Sense] = []
            targets: list[str] = []
            for s in r["s"]:
                if s["fo"]:  # «plural of dictionary», «alternative spelling of …» -> таблица форм
                    if not SKIP_FORM_TAGS & set(s["t"]):  # «don't» — archaic form of «done»
                        for target in s["fo"]:
                            if target != word:
                                targets.append(target)
                                forms_raw.append((word, target, " ".join(s["t"])))
                    continue
                if not s["g"] or "no-gloss" in s["t"]:
                    continue
                sense = Sense(pos, s["g"], s["t"],
                              [e[0] for e in sorted(s["ex"], key=lambda e: e[1] != "example")][:MAX_EXAMPLES],
                              s["syn"])
                for w, _ in s["tr"]:
                    add_translation(sense, w)
                senses.append(sense)

            assign_translations(senses, r["tr"])
            senses = [s for s in senses
                      if s.tr or not (DROP_SENSE_TAGS & set(s.tags)) and pos not in NEED_RU_POS]

            # Запись-ссылка («neighbor» -> «neighbour»): её формы («neighbors») ведут к цели,
            # а не к самой записи — иначе опечатка «we'll» (= well) получала бы форму «better».
            owners = [word] if senses or not targets else targets
            for form, tags in r["f"]:
                if (form != word and WORD_RE.match(form) and not SKIP_FORM_TAGS & set(tags)
                        and not form.startswith(("more ", "most "))):
                    for owner in owners:
                        forms_raw.append((form, owner, " ".join(tags)))

            if not senses:
                stats["no_senses"] += 1
                continue

            lemma = lemmas.setdefault(word, Lemma(word))
            lemma.senses.extend(senses)
            lemma.synonyms.extend(r["syn"])
            for ipa, tags in r["ipa"]:
                lemma.ipa.append((ipa, tags, ipa_order))
                ipa_order += 1

    log.info("Записей: %d, лемм-кандидатов: %d, ссылок форм: %d",
             stats["records"], len(lemmas), len(forms_raw))

    # wordfreq не различает регистр: у фамилии «Children» частотность слова «children».
    # Поэтому имя без перевода берём, только если в нижнем регистре это не обычное слово.
    common_lc = {f.lower() for f, _, _ in forms_raw}
    common_lc.update(w.lower() for w, l in lemmas.items() if any(s.pos != "name" for s in l.senses))

    # Отбор лемм: с русским переводом — всегда; без него — только достаточно частотные.
    kept: list[Lemma] = []
    for lemma in lemmas.values():
        z = zipf_of(lemma.word)
        if z < NAME_MIN_ZIPF or lemma.word.lower() in common_lc:
            lemma.senses = [s for s in lemma.senses if s.pos != "name" or s.tr]
            if not lemma.senses:
                continue
        multi = " " in lemma.word
        if lemma.has_ru or (not multi and z >= min_zipf) or (
                multi and lemma.word.count(" ") <= 3 and z >= min_phrase_zipf):
            if len(lemma.senses) > MAX_SENSES:
                head = lemma.senses[:MAX_SENSES]
                lemma.senses = head + [s for s in lemma.senses[MAX_SENSES:] if s.tr]
            kept.append(lemma)
    kept.sort(key=lambda l: l.word)
    log.info("Оставлено лемм: %d (с русским переводом: %d)", len(kept), sum(l.has_ru for l in kept))

    cefr = load_cefr(log)
    write_db(kept, forms_raw, cefr, src)
    report_coverage(top_n_list("en", 10000))


def write_db(kept: list[Lemma], forms_raw: list[tuple[str, str, str]], cefr: dict[str, str], src: Source) -> None:
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    out, tmp = DATA_DIR / "dict.db", DATA_DIR / "dict.db.tmp"
    tmp.unlink(missing_ok=True)
    db = sqlite3.connect(tmp)
    db.executescript("""
        PRAGMA journal_mode = OFF;
        PRAGMA synchronous = OFF;
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE lemmas (
            id INTEGER PRIMARY KEY,
            lemma TEXT NOT NULL,          -- как в Викисловаре (регистр важен: Polish / polish)
            lemma_lc TEXT NOT NULL,
            first_word TEXT NOT NULL,     -- первое слово в нижнем регистре, для фразовых глаголов
            word_count INTEGER NOT NULL,
            ipa TEXT,
            cefr TEXT,
            zipf REAL NOT NULL,           -- частотность wordfreq (0..8)
            synonyms TEXT                 -- JSON-массив или NULL
        );
        CREATE TABLE senses (
            id INTEGER PRIMARY KEY,
            lemma_id INTEGER NOT NULL REFERENCES lemmas(id),
            ord INTEGER NOT NULL,         -- порядок значения в Викисловаре
            pos TEXT NOT NULL,            -- часть речи wiktextract: noun, verb, adj, adv, ...
            definition_en TEXT NOT NULL,
            translations_ru TEXT,         -- JSON-массив или NULL
            examples TEXT                 -- JSON-массив или NULL
        );
        CREATE TABLE forms (
            form_lc TEXT NOT NULL,
            lemma_id INTEGER NOT NULL REFERENCES lemmas(id),
            tags TEXT NOT NULL,           -- теги wiktextract через пробел: plural, past, ...
            PRIMARY KEY (form_lc, lemma_id)
        ) WITHOUT ROWID;
    """)

    def js(items: list[str]) -> str | None:
        return json.dumps(items, ensure_ascii=False, separators=(",", ":")) if items else None

    by_exact: dict[str, int] = {}
    by_lc: dict[str, list[int]] = defaultdict(list)
    sense_rows = []
    lemma_rows = []
    with_cefr = 0
    for lemma_id, l in enumerate(kept, start=1):
        lc = l.word.lower()
        by_exact[l.word] = lemma_id
        by_lc[lc].append(lemma_id)
        seen = {lc}
        syns = []
        for s in [x for sense in l.senses for x in sense.synonyms] + l.synonyms:
            s = _clean(s)
            if s.lower() not in seen and not s.startswith("Thesaurus:") and WORD_RE.match(s):
                seen.add(s.lower())
                syns.append(s)
        level = cefr.get(lc)
        with_cefr += level is not None
        lemma_rows.append((lemma_id, l.word, lc, lc.split(" ")[0], lc.count(" ") + 1,
                           pick_ipa(l.ipa), level, round(zipf_of(l.word), 2), js(syns[:MAX_SYNONYMS])))
        for ord_, s in enumerate(l.senses):
            sense_rows.append((lemma_id, ord_, s.pos, s.gloss, js(s.tr), js(s.examples)))
    db.executemany("INSERT INTO lemmas VALUES (?,?,?,?,?,?,?,?,?)", lemma_rows)
    db.executemany("INSERT INTO senses (lemma_id, ord, pos, definition_en, translations_ru, examples) "
                   "VALUES (?,?,?,?,?,?)", sense_rows)

    form_rows = {}
    for form, target, tags in forms_raw:
        ids = [by_exact[target]] if target in by_exact else by_lc.get(target.lower(), [])
        flc = form.lower()
        for lemma_id in ids:
            if kept[lemma_id - 1].word.lower() != flc:
                form_rows.setdefault((flc, lemma_id), tags)
    db.executemany("INSERT INTO forms VALUES (?,?,?)", [(f, i, t) for (f, i), t in form_rows.items()])

    meta = {
        "schema_version": str(SCHEMA_VERSION),
        "built_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "source": src.url,
        "source_version": src.version,
        "lemmas": str(len(lemma_rows)),
        "senses": str(len(sense_rows)),
        "forms": str(len(form_rows)),
        "license": "Wiktionary (CC BY-SA 4.0, GFDL) via wiktextract/kaikki.org; "
                   "CEFR-J Vocabulary Profile 1.5 (Tono Laboratory, TUFS); "
                   "Octanove Vocabulary Profile C1/C2 1.0 (CC BY-SA 4.0); wordfreq (CC BY-SA 4.0)",
    }
    db.executemany("INSERT INTO meta VALUES (?,?)", meta.items())
    db.executescript("""
        CREATE INDEX ix_lemmas_lemma ON lemmas(lemma);
        CREATE INDEX ix_lemmas_lemma_lc ON lemmas(lemma_lc);
        CREATE INDEX ix_lemmas_phrase ON lemmas(first_word) WHERE word_count > 1;
        CREATE INDEX ix_senses_lemma ON senses(lemma_id, ord);
        ANALYZE;
    """)
    db.commit()
    db.execute("VACUUM")
    db.close()
    tmp.replace(out)

    size_mb = out.stat().st_size / 2**20
    log.info("dict.db: %.1f МБ; лемм %d, значений %d, форм %d, с CEFR %d",
             size_mb, len(lemma_rows), len(sense_rows), len(form_rows), with_cefr)
    if size_mb > MAX_DB_MB:
        raise RuntimeError(f"dict.db {size_mb:.0f} МБ > {MAX_DB_MB} МБ — поднимите --min-zipf")


def report_coverage(top: list[str]) -> None:
    """Предварительная оценка критерия покрытия (без правил лемматизации)."""
    db = sqlite3.connect(DATA_DIR / "dict.db")
    lemma_set = {r[0] for r in db.execute("SELECT lemma_lc FROM lemmas")}
    form_set = {r[0] for r in db.execute("SELECT form_lc FROM forms")}
    words = [w for w in top if TOKEN_RE.fullmatch(w.replace("'", "a"))]
    missing = [w for w in words if w not in lemma_set and w not in form_set]
    with_ru = {r[0] for r in db.execute(
        "SELECT DISTINCT l.lemma_lc FROM lemmas l JOIN senses s ON s.lemma_id = l.id "
        "WHERE s.translations_ru IS NOT NULL")}
    direct_ru = sum(w in with_ru for w in words)
    log.info("Покрытие топ-%d wordfreq (только слова): статья найдена %.1f%%, с русским переводом у самой леммы %.1f%%",
             len(words), 100 * (1 - len(missing) / len(words)), 100 * direct_ru / len(words))
    log.info("Не найдены (первые 40): %s", ", ".join(missing[:40]))
    db.close()


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--source", choices=SOURCES, default="mirror")
    ap.add_argument("--min-zipf", type=float, default=2.0,
                    help="минимальная частотность для слов без русского перевода")
    ap.add_argument("--min-phrase-zipf", type=float, default=3.0,
                    help="то же для выражений из нескольких слов")
    args = ap.parse_args()

    t0 = time.monotonic()
    src = SOURCES[args.source]
    slim = extract_english(src, ensure_source(src))
    build(slim, src, args.min_zipf, args.min_phrase_zipf)
    log.info("Готово за %.1f мин", (time.monotonic() - t0) / 60)


if __name__ == "__main__":
    main()
