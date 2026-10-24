# DataBuilder — подготовка данных HoverTranslate

Скрипты запускаются **один раз** на машине разработчика. Результат кладётся в `data/` в корне репозитория
и в git не попадает (см. `.gitignore`); установщик берёт его оттуда.

| Результат | Скрипт | Размер | Время |
|---|---|---|---|
| `data/dict.db` | `build_dict.py` | 86 МБ | ~4 мин + загрузка 2,9 ГБ |
| `data/models/opus-mt-en-ru/` | `export_model.py` | 224 МБ | ~2 мин |
| `data/models/ppocr/` (запасной OCR, необязательно) | `export_ppocr.py` | 15,6 МБ | секунды + загрузка 15 МБ |

## Требования

- Windows 10/11 x64, Python **3.12** (3.13+ не проверялся: для torch 2.9 нужны колёса под эту версию).
- ~8 ГБ свободного места: дамп Викисловаря 2,9 ГБ + компактный кэш ~0,4 ГБ + fp32-модель ~0,9 ГБ (удаляется).
- Путь к репозиторию **без кириллицы и не в OneDrive** (sentencepiece на Windows плохо переносит не-ASCII пути,
  а OneDrive пытается синхронизировать гигабайты кэша). Проект живёт в `C:\projs\HoverTranslate`.

## Запуск

```powershell
cd C:\projs\HoverTranslate\tools\DataBuilder
py -3.12 -m venv .venv          # или полный путь к python.exe 3.12
.venv\Scripts\python.exe -m pip install -r requirements.txt

.venv\Scripts\python.exe export_model.py   # модель перевода
.venv\Scripts\python.exe build_dict.py     # словарь
.venv\Scripts\python.exe export_ppocr.py   # необязательно: модели запасного OCR (PaddleOCR)
```

`export_ppocr.py` скачивает колесо `rapidocr-onnxruntime` (без зависимостей) и достаёт из него детектор и
распознаватель PP-OCRv4; пакету нужен только `pip`, торч и прочее не нужны.

Всё скачанное лежит в `tools/DataBuilder/.cache/` (дамп, CSV CEFR, кэш HuggingFace). Повторный запуск
ничего не скачивает заново; `build_dict.py` при повторе пропускает долгое извлечение и пересобирает
только `dict.db` (~1 мин), что удобно для подбора фильтров.

### Параметры

`build_dict.py`
- `--source mirror|kaikki` — источник дампа (по умолчанию `mirror`, см. ниже).
- `--min-zipf 2.0` — минимальная частотность (шкала Zipf wordfreq) для слов **без** русского перевода.
  Слова с русским переводом попадают в словарь всегда. Если база превысит 150 МБ, скрипт упадёт с подсказкой поднять порог.
- `--min-phrase-zipf 3.0` — то же для выражений из 2–4 слов.

`export_model.py`
- `--keep-fp32` — не удалять промежуточный fp32-экспорт (`.cache/onnx-fp32`).
- `--verify-only` — только прогнать проверку уже собранной модели.

## Что делает `build_dict.py`

1. Скачивает дамп wiktextract (английский Викисловарь, разобранный в JSONL) и проверяет SHA-256.
2. Извлекает английские записи (`lang_code == "en"`) в компактный кэш `en-slim-<версия>.jsonl`.
3. Для каждой записи:
   - **IPA**: американское произношение (`US`/`General-American`), иначе без пометок, иначе первое; фонематическая `/…/` предпочтительнее фонетической `[…]`;
   - **определения**: `raw_glosses` (с пометками вроде «(computing)»); значения с тегами `obsolete`/`archaic` отбрасываются, если у них нет русского перевода;
   - **русские переводы**: `translations` с `lang_code == "ru"` на уровне значения и на уровне слова.
     Переводы уровня слова в Викисловаре подписаны кратким описанием значения («not imprisoned») —
     они привязываются к значению по пересечению слов подписи и определения.
     Знаки ударения удаляются (`слова́рь` → `словарь`);
   - **примеры**: до 2 на значение, сначала учебные (`example`), затем короткие цитаты (≤160 символов);
   - **синонимы**: уровня значения и слова, до 10;
   - записи, состоящие только из `form_of`/`alt_of` («plural of dictionary», «alternative spelling of…»),
     а также поле `forms` попадают в таблицу **словоформ → лемма**;
   - из переводов убираются латинские пометки (`сдать pf trans.` → `сдать`), а латинские буквы-двойники
     внутри русских слов заменяются кириллицей (`Биaнка` → `Бианка`);
   - ссылки с тегами `obsolete`/`archaic` в таблицу форм не попадают («don't» не ведёт к «done»);
     формы записи-ссылки («neighbor» → «neighbour») ведут к цели («neighbors» → «neighbour»);
   - части речи `prefix`, `suffix`, `symbol`, `character` и т. п. пропускаются; пословицы — только с русским переводом;
     имена собственные без перевода — только если Zipf ≥ 4 и это не обычное слово в нижнем регистре
     (`America` остаётся, фамилия `Children` — нет: wordfreq не различает регистр).
4. Лемма попадает в базу, если у неё есть русский перевод **или** частотность ≥ `--min-zipf`.
5. Добавляет CEFR (минимальный уровень слова по всем частям речи) и частотность Zipf.
6. Пишет SQLite, строит индексы, `VACUUM`, печатает покрытие 10 000 самых частотных слов.

### Известные ограничения данных

- Привязка переводов уровня слова к значениям эвристическая: иногда перевод оказывается не у того значения
  (например, «blunder» — «двигаться ощупью» у шахматного значения).
- У части слов в английском Викисловаре нет нужного перевода («free» без «бесплатный», «America» без «Америка»).
- ~30 из 137 762 переводов содержат остатки английских пометок (`in different meanings закрыть`).

### Схема `dict.db` (schema_version = 1)

```sql
meta   (key TEXT PRIMARY KEY, value TEXT)            -- источник, версия дампа, дата, лицензии, счётчики
lemmas (id, lemma, lemma_lc, first_word, word_count, ipa, cefr, zipf, synonyms /*JSON*/)
senses (id, lemma_id, ord, pos, definition_en, translations_ru /*JSON*/, examples /*JSON*/)
forms  (form_lc, lemma_id, tags)  PRIMARY KEY (form_lc, lemma_id)  WITHOUT ROWID

-- индексы
lemmas(lemma), lemmas(lemma_lc), lemmas(first_word) WHERE word_count > 1,   -- для «give up», «look forward to»
senses(lemma_id, ord)
```

`lemma` хранится с регистром (`Polish` и `polish` — разные леммы), поиск идёт по `lemma_lc`.
`pos` — коды wiktextract (`noun`, `verb`, `adj`, `adv`, `prep`, `pron`, `conj`, `intj`, `det`, `num`,
`phrase`, `prep_phrase`, `contraction`, `name`, …); перевод в русские подписи — в приложении.

## Что делает `export_model.py`

1. `optimum-cli export onnx --task text2text-generation-with-past --no-post-process` →
   `encoder_model.onnx`, `decoder_model.onnx` (первый шаг), `decoder_with_past_model.onnx` (шаги с KV-кэшем).
   Склеенный `decoder_model_merged.onnx` не используется: его подграфы `If` ненадёжно квантуются.
2. Динамическое квантование int8: `per_channel=True, reduce_range=True`, операции `MatMul` и `Gather`.
   - **`reduce_range` обязателен.** Без него per-channel int8 переполняется в ядрах u8×s8 на CPU без VNNI,
     и перевод превращается в бессмыслицу («Постарайтесь, чтобы это поместили в поместье»). Проверено.
   - Квантование `Gather` (эмбеддинги словаря 62 518 × 512) уменьшает модель с ~500 до 220 МБ без потери качества.
3. Копирует `source.spm`, `target.spm`, `vocab.json`, `config.json`, `generation_config.json`.
4. Проверка: 5 предложений из `test_sentences.txt` переводятся PyTorch (beam 4 и greedy) и
   ONNX int8 greedy-декодером на onnxruntime (тот же алгоритм, что будет в C#). Скрипт падает,
   если среднее сходство с PyTorch greedy < 0,85.
5. Пишет `tokenizer_golden.json` — эталонные куски и id токенов HF-токенизатора для тестов C#-токенизатора
   (Marian переводит куски SentencePiece в id через `vocab.json`, а не через id из `.spm`).

Важные параметры декодирования (из `config.json`): `decoder_start_token_id = pad = 62517`, `eos = 0`;
логит `pad` нужно маскировать на каждом шаге (`bad_words_ids` в `generation_config.json`).

## Источники и лицензии

| Данные | Источник | Лицензия |
|---|---|---|
| Английский Викисловарь | Wiktionary через [wiktextract](https://github.com/tatuylonen/wiktextract) / [kaikki.org](https://kaikki.org/dictionary/rawdata.html) | CC BY-SA 4.0 и GFDL (как у Викисловаря) |
| Зеркало дампа | [huggingface.co/datasets/DataDock/wiktextract](https://huggingface.co/datasets/DataDock/wiktextract), файл `2026-09-02.jsonl.gz` | CC BY-SA 4.0 |
| CEFR A1–B2 | CEFR-J Vocabulary Profile 1.5, Tono Laboratory, TUFS — через [Open Language Profiles](https://github.com/openlanguageprofiles/olp-en-cefrj) | бесплатно для исследований и коммерческого использования при условии ссылки на источник |
| CEFR C1–C2 | Octanove Vocabulary Profile C1/C2 1.0, Octanove Labs — там же | CC BY-SA 4.0 |
| Частотность | [wordfreq](https://github.com/rspeer/wordfreq) 3.1 | код Apache 2.0, данные CC BY-SA 4.0 |
| Модель перевода | [Helsinki-NLP/opus-mt-en-ru](https://huggingface.co/Helsinki-NLP/opus-mt-en-ru) | CC BY 4.0 |

Следствия для приложения:
- `dict.db` — производная работа от Викисловаря, поэтому распространяется на условиях **CC BY-SA 4.0**;
  в окне «О программе» и в установщике нужно указать: Wiktionary contributors, wiktextract/kaikki.org (Tatu Ylonen),
  CEFR-J (Yukio Tono, Tokyo University of Foreign Studies), Octanove Labs, wordfreq (Robyn Speer), Helsinki-NLP / OPUS-MT.
- Требуемые цитаты:
  - *The CEFR-J Wordlist Version 1.5. Compiled by Yukio Tono, Tokyo University of Foreign Studies.*
  - *Tiedemann J., Thottingal S. OPUS-MT — Building open translation services for the World. EAMT 2020.*
  - *Ylonen T. Wiktextract: Wiktionary as Machine-Readable Structured Data. LREC 2022.*

### Почему зеркало, а не kaikki.org

Спецификация предписывает kaikki.org, но сервер отдавал файл (3,3 ГБ) со скоростью ~400 Б/с и рвал соединения.
Зеркало на HuggingFace содержит **тот же дамп** (метка `2026-09-02_2026-10-03_1a05e46_e3d6d4e` — дамп Викисловаря
от 2026-09-02, обработан wiktextract 2026-10-03, тогда же опубликован на kaikki.org), но в виде полного сырого
файла со всеми языками — английские записи отбираются по `lang_code`. Плюсы: версия зафиксирована
(сборка воспроизводима) и проверяется по SHA-256. Если kaikki.org доступен быстро: `--source kaikki`
(файл `kaikki.org-dictionary-English.jsonl`, всегда последняя версия, формат тот же).
