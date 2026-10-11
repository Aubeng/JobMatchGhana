"""BERT+CRF entity tagger (S&D Design §6.3.2).

Subword hidden states from the encoder are reduced to one vector per word (its first subword),
projected to BIO tag emissions, and decoded by a CRF. Working at word level keeps labels aligned
with the annotation format and makes the CRF's transitions meaningful.
"""
from __future__ import annotations

import json
from pathlib import Path

import torch
from torch import nn
from transformers import AutoModel, AutoTokenizer

from .annotation import BIO_TAGS, words
from .crf import CRF, bio_allowed_transitions


class BertCrfTagger(nn.Module):
    def __init__(self, encoder: nn.Module, tags: tuple[str, ...] = BIO_TAGS, dropout: float = 0.1):
        super().__init__()
        self.tags = tuple(tags)
        self.encoder = encoder
        self.dropout = nn.Dropout(dropout)
        self.classifier = nn.Linear(encoder.config.hidden_size, len(self.tags))
        allowed, allowed_start = bio_allowed_transitions(self.tags)
        self.crf = CRF(len(self.tags), allowed, allowed_start)

    @classmethod
    def from_base(cls, base_model: str) -> "BertCrfTagger":
        return cls(AutoModel.from_pretrained(base_model))

    def emissions(self, input_ids, attention_mask, word_index, word_mask) -> torch.Tensor:
        hidden = self.encoder(input_ids=input_ids, attention_mask=attention_mask).last_hidden_state
        gathered = torch.gather(hidden, 1, word_index.unsqueeze(-1).expand(-1, -1, hidden.size(-1)))
        return self.classifier(self.dropout(gathered)) * word_mask.unsqueeze(-1)

    def loss(self, batch: dict) -> torch.Tensor:
        em = self.emissions(batch["input_ids"], batch["attention_mask"], batch["word_index"], batch["word_mask"])
        return self.crf(em, batch["tags"], batch["word_mask"])

    @torch.no_grad()
    def predict(self, batch: dict) -> tuple[list[list[str]], list[list[float]]]:
        """Tags per word, plus each word's CRF marginal probability for its decoded tag (confidence)."""
        em = self.emissions(batch["input_ids"], batch["attention_mask"], batch["word_index"], batch["word_mask"])
        paths = self.crf.decode(em, batch["word_mask"])
        # Marginals, not a softmax over emissions: in a CRF the transitions carry part of the
        # evidence, so emission scores alone are uncalibrated (a correct "carpentar" -> B-TRADE
        # scored 0.38 that way).
        probs = self.crf.marginals(em, batch["word_mask"])
        tags, confs = [], []
        for b, path in enumerate(paths):
            tags.append([self.tags[i] for i in path])
            confs.append([round(float(probs[b, t, i]), 4) for t, i in enumerate(path)])
        return tags, confs

    def save(self, directory: Path, tokenizer) -> None:
        directory = Path(directory)
        directory.mkdir(parents=True, exist_ok=True)
        self.encoder.save_pretrained(directory / "encoder")
        tokenizer.save_pretrained(directory / "encoder")
        torch.save({"classifier": self.classifier.state_dict(), "crf": self.crf.state_dict()},
                   directory / "head.pt")
        (directory / "tags.json").write_text(json.dumps(list(self.tags)), encoding="utf-8")

    @classmethod
    def load(cls, directory: Path) -> tuple["BertCrfTagger", object]:
        directory = Path(directory)
        tags = tuple(json.loads((directory / "tags.json").read_text(encoding="utf-8")))
        model = cls(AutoModel.from_pretrained(directory / "encoder"), tags)
        head = torch.load(directory / "head.pt", map_location="cpu", weights_only=True)
        model.classifier.load_state_dict(head["classifier"])
        model.crf.load_state_dict(head["crf"])
        model.eval()
        return model, AutoTokenizer.from_pretrained(directory / "encoder")


def encode_words(tokenizer, texts: list[str], max_length: int,
                 tag_seqs: list[list[str]] | None = None, tags: tuple[str, ...] = BIO_TAGS) -> dict:
    """Tokenise word-split texts and build the word->first-subword index used by the tagger.

    Words cut off by ``max_length`` are dropped from the batch (``n_words`` records how many
    survived, so callers can treat the rest as "O")."""
    word_lists = [[w for w, _, _ in words(t)] or ["."] for t in texts]
    enc = tokenizer(word_lists, is_split_into_words=True, truncation=True, max_length=max_length,
                    padding=True, return_tensors="pt")
    tag_to_id = {t: i for i, t in enumerate(tags)}
    firsts: list[list[int]] = []
    for b in range(len(texts)):
        seen, first = set(), []
        for pos, wid in enumerate(enc.word_ids(b)):
            if wid is not None and wid not in seen:
                seen.add(wid)
                first.append(pos)
        firsts.append(first)
    width = max(1, max(len(f) for f in firsts))
    word_index = torch.zeros(len(texts), width, dtype=torch.long)
    word_mask = torch.zeros(len(texts), width, dtype=torch.bool)
    tag_ids = torch.zeros(len(texts), width, dtype=torch.long)
    for b, first in enumerate(firsts):
        word_index[b, :len(first)] = torch.tensor(first, dtype=torch.long)
        word_mask[b, :len(first)] = True
        if tag_seqs is not None:
            ids = [tag_to_id[t] for t in tag_seqs[b][:len(first)]]
            tag_ids[b, :len(ids)] = torch.tensor(ids, dtype=torch.long)
    batch = {"input_ids": enc["input_ids"], "attention_mask": enc["attention_mask"],
             "word_index": word_index, "word_mask": word_mask,
             "n_words": [len(f) for f in firsts]}
    if tag_seqs is not None:
        batch["tags"] = tag_ids
    return batch
