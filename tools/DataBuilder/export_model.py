"""Экспорт Helsinki-NLP/opus-mt-en-ru в ONNX (encoder + decoder + decoder_with_past) и квантование int8.

Результат: data/models/opus-mt-en-ru/
    encoder_model.onnx, decoder_model.onnx, decoder_with_past_model.onnx  — int8
    source.spm, target.spm, vocab.json, config.json, generation_config.json
    tokenizer_golden.json — эталонная токенизация для тестов C#-токенизатора

Запуск:  .venv\\Scripts\\python.exe export_model.py [--keep-fp32]
"""

from __future__ import annotations

import argparse
import difflib
import json
import logging
import shutil
import subprocess
import sys
import time
from pathlib import Path

from common import CACHE_DIR, MODELS_DIR, TOOLS_DIR, human_size, setup_logging

MODEL_ID = "Helsinki-NLP/opus-mt-en-ru"
OUT_DIR = MODELS_DIR / "opus-mt-en-ru"
FP32_DIR = CACHE_DIR / "onnx-fp32"
ONNX_FILES = ("encoder_model.onnx", "decoder_model.onnx", "decoder_with_past_model.onnx")
TOKENIZER_FILES = ("source.spm", "target.spm", "vocab.json")

# Дополнительные строки для эталонной токенизации: пунктуация, числа, Unicode, апострофы.
EXTRA_GOLDEN = [
    "Hello, world!",
    "It's 3:45 p.m. — don't be late…",
    "“Quoted” text with ‘curly’ apostrophes and naïve café.",
    "HP: 120/150  Mana: 30%  Gold: 1,250",
    "well-known state-of-the-art e-mail",
    "",
]

log = setup_logging()


def export_fp32() -> None:
    if all((FP32_DIR / f).exists() for f in ONNX_FILES):
        log.info("ONNX fp32 уже экспортирован: %s", FP32_DIR)
        return
    cli = Path(sys.executable).parent / "optimum-cli.exe"
    cmd = [
        str(cli), "export", "onnx",
        "--model", MODEL_ID,
        "--task", "text2text-generation-with-past",
        "--opset", "17",
        "--no-post-process",  # не склеивать декодеры в decoder_model_merged (подграфы If плохо квантуются)
        str(FP32_DIR),
    ]
    log.info("Экспорт: %s", " ".join(cmd))
    subprocess.run(cmd, check=True)


def quantize() -> None:
    from onnxruntime.quantization import QuantType, quantize_dynamic

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    root = logging.getLogger()
    for name in ONNX_FILES:
        src, dst = FP32_DIR / name, OUT_DIR / name
        log.info("Квантование %s (%s)", name, human_size(src))
        # Квантователь пишет тысячи INFO-строк в корневой логгер — приглушаем.
        root.setLevel(logging.WARNING)
        try:
            # reduce_range обязателен: per-channel int8 без него переполняется в u8×s8
            # на CPU без VNNI, и перевод превращается в мусор (проверено).
            # Gather квантует эмбеддинги: ~500 МБ -> ~220 МБ без потери качества.
            quantize_dynamic(
                model_input=src,
                model_output=dst,
                weight_type=QuantType.QInt8,
                per_channel=True,
                reduce_range=True,
                op_types_to_quantize=["MatMul", "Gather"],
            )
        finally:
            root.setLevel(logging.INFO)
        log.info("  -> %s", human_size(dst))


def copy_tokenizer_and_config() -> None:
    from transformers import MarianTokenizer

    tok = MarianTokenizer.from_pretrained(MODEL_ID)
    tmp = CACHE_DIR / "tokenizer"
    tok.save_pretrained(tmp)
    for name in TOKENIZER_FILES:
        shutil.copy2(tmp / name, OUT_DIR / name)
    for name in ("config.json", "generation_config.json"):
        shutil.copy2(FP32_DIR / name, OUT_DIR / name)


class OnnxMarian:
    """Минимальный greedy-декодер на onnxruntime — тот же алгоритм будет в C#."""

    def __init__(self, model_dir: Path, config: dict) -> None:
        import onnxruntime as ort

        opts = ort.SessionOptions()
        opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        mk = lambda f: ort.InferenceSession(str(model_dir / f), opts, providers=["CPUExecutionProvider"])
        self.encoder = mk("encoder_model.onnx")
        self.decoder = mk("decoder_model.onnx")
        self.decoder_past = mk("decoder_with_past_model.onnx")
        self.start_id = config["decoder_start_token_id"]
        self.eos_id = config["eos_token_id"]
        self.pad_id = config["pad_token_id"]

    def generate(self, input_ids: list[int], max_len: int = 256) -> list[int]:
        import numpy as np

        ids = np.array([input_ids], dtype=np.int64)
        mask = np.ones_like(ids)
        enc = self.encoder.run(None, {"input_ids": ids, "attention_mask": mask})[0]

        out_names = [o.name for o in self.decoder.get_outputs()]
        res = self.decoder.run(None, {
            "input_ids": np.array([[self.start_id]], dtype=np.int64),
            "encoder_attention_mask": mask,
            "encoder_hidden_states": enc,
        })
        outputs = dict(zip(out_names, res))
        # present.N.{decoder|encoder}.{key|value} -> past_key_values.N.{...}
        past = {k.replace("present", "past_key_values"): v for k, v in outputs.items() if k.startswith("present")}
        past_inputs = {i.name for i in self.decoder_past.get_inputs()}
        past_out_names = [o.name for o in self.decoder_past.get_outputs()]

        result: list[int] = []
        logits = outputs["logits"]
        for _ in range(max_len):
            step = logits[0, -1].copy()
            step[self.pad_id] = -np.inf  # bad_words_ids из generation_config
            token = int(step.argmax())
            if token == self.eos_id:
                break
            result.append(token)
            feeds = {"input_ids": np.array([[token]], dtype=np.int64), "encoder_attention_mask": mask}
            feeds.update({k: v for k, v in past.items() if k in past_inputs})
            res = self.decoder_past.run(None, feeds)
            outs = dict(zip(past_out_names, res))
            logits = outs["logits"]
            # Кэш энкодера не меняется — обновляем только self-attention часть декодера.
            for k, v in outs.items():
                if k.startswith("present"):
                    past[k.replace("present", "past_key_values")] = v
        return result


def verify() -> None:
    import torch
    from transformers import MarianMTModel, MarianTokenizer

    sentences = [s.strip() for s in (TOOLS_DIR / "test_sentences.txt").read_text("utf-8").splitlines() if s.strip()]
    config = json.loads((OUT_DIR / "config.json").read_text("utf-8"))
    tok = MarianTokenizer.from_pretrained(MODEL_ID)
    pt = MarianMTModel.from_pretrained(MODEL_ID).eval()
    onnx = OnnxMarian(OUT_DIR, config)

    golden: dict = {"source": [], "target": []}
    log.info("Проверка перевода (%d предложений):", len(sentences))
    similarities: list[float] = []
    times: list[float] = []
    for s in sentences:
        input_ids = tok(s).input_ids
        with torch.no_grad():
            pt_greedy = tok.decode(pt.generate(**tok(s, return_tensors="pt"), num_beams=1, max_new_tokens=256)[0],
                                   skip_special_tokens=True)
            pt_beam = tok.decode(pt.generate(**tok(s, return_tensors="pt"), num_beams=4, max_new_tokens=256)[0],
                                 skip_special_tokens=True)
        onnx.generate(input_ids)  # прогрев
        t0 = time.perf_counter()
        out_ids = onnx.generate(input_ids)
        ms = (time.perf_counter() - t0) * 1000
        onnx_text = tok.decode(out_ids, skip_special_tokens=True)
        log.info("EN:          %s", s)
        log.info("PyTorch b=4: %s", pt_beam)
        log.info("PyTorch b=1: %s", pt_greedy)
        sim = difflib.SequenceMatcher(None, onnx_text, pt_greedy).ratio()
        similarities.append(sim)
        times.append(ms)
        log.info("ONNX int8:   %s   [%.0f мс, %d слов, сходство с PyTorch %.2f]",
                 onnx_text, ms, len(s.split()), sim)
        golden["target"].append({"ids": out_ids, "text": onnx_text})

    # int8 допускает мелкие расхождения с fp32, но не другой смысл.
    avg = sum(similarities) / len(similarities)
    log.info("Среднее сходство с PyTorch greedy: %.2f; среднее время: %.0f мс", avg, sum(times) / len(times))
    if avg < 0.85 or min(similarities) < 0.6:
        raise RuntimeError("Квантованная модель заметно расходится с исходной — проверьте параметры квантования")

    for s in sentences + EXTRA_GOLDEN:
        golden["source"].append({
            "text": s,
            "pieces": tok.tokenize(s),
            "ids": tok(s).input_ids,  # с завершающим </s>
        })
    golden["special"] = {
        "eos": config["eos_token_id"], "pad": config["pad_token_id"],
        "unk": tok.unk_token_id, "decoder_start": config["decoder_start_token_id"],
    }
    (OUT_DIR / "tokenizer_golden.json").write_text(json.dumps(golden, ensure_ascii=False, indent=1), "utf-8")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--keep-fp32", action="store_true", help="не удалять промежуточный fp32-экспорт")
    ap.add_argument("--verify-only", action="store_true", help="только проверить уже собранную модель")
    args = ap.parse_args()

    if not args.verify_only:
        export_fp32()
        quantize()
        copy_tokenizer_and_config()
    verify()
    if not args.keep_fp32 and not args.verify_only:
        shutil.rmtree(FP32_DIR, ignore_errors=True)
    log.info("Готово: %s (%s)", OUT_DIR, human_size(OUT_DIR))


if __name__ == "__main__":
    main()
