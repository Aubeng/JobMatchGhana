"""Linear-chain conditional random field over word-level emissions (the "CRF" in BERT+CRF).

The CRF scores whole tag sequences (emission + transition scores) instead of tagging each word
independently, so it learns that I-SKILL_ITEM follows B-SKILL_ITEM and never follows O. Invalid
BIO transitions are additionally forbidden at decode time, so every output sequence is well formed.
"""
from __future__ import annotations

import torch
from torch import nn

_FORBIDDEN = -1e4


def bio_allowed_transitions(tags: list[str] | tuple[str, ...]) -> tuple[torch.Tensor, torch.Tensor]:
    """(allowed[i, j] for i -> j, allowed_start[j]) under BIO rules: I-X may only follow B-X/I-X."""
    n = len(tags)
    allowed = torch.ones(n, n, dtype=torch.bool)
    start = torch.ones(n, dtype=torch.bool)
    for j, tj in enumerate(tags):
        if tj.startswith("I-"):
            start[j] = False
            for i, ti in enumerate(tags):
                allowed[i, j] = ti[2:] == tj[2:] and ti[:2] in ("B-", "I-")
    return allowed, start


class CRF(nn.Module):
    def __init__(self, num_tags: int, allowed: torch.Tensor | None = None,
                 allowed_start: torch.Tensor | None = None):
        super().__init__()
        self.num_tags = num_tags
        self.start_transitions = nn.Parameter(torch.empty(num_tags).uniform_(-0.1, 0.1))
        self.end_transitions = nn.Parameter(torch.empty(num_tags).uniform_(-0.1, 0.1))
        self.transitions = nn.Parameter(torch.empty(num_tags, num_tags).uniform_(-0.1, 0.1))
        if allowed is None:
            allowed = torch.ones(num_tags, num_tags, dtype=torch.bool)
        if allowed_start is None:
            allowed_start = torch.ones(num_tags, dtype=torch.bool)
        self.register_buffer("allowed", allowed)
        self.register_buffer("allowed_start", allowed_start)

    def _masked_params(self) -> tuple[torch.Tensor, torch.Tensor]:
        trans = self.transitions.masked_fill(~self.allowed, _FORBIDDEN)
        start = self.start_transitions.masked_fill(~self.allowed_start, _FORBIDDEN)
        return trans, start

    def forward(self, emissions: torch.Tensor, tags: torch.Tensor, mask: torch.Tensor) -> torch.Tensor:
        """Mean negative log-likelihood. emissions (B,T,K), tags (B,T), mask (B,T) with mask[:,0] true."""
        mask = mask.bool()
        trans, start = self._masked_params()
        return (self._log_partition(emissions, mask, trans, start)
                - self._score(emissions, tags, mask, trans, start)).mean()

    def _score(self, emissions, tags, mask, trans, start):
        batch, seq_len, _ = emissions.shape
        idx = torch.arange(batch, device=emissions.device)
        score = start[tags[:, 0]] + emissions[idx, 0, tags[:, 0]]
        for t in range(1, seq_len):
            step = trans[tags[:, t - 1], tags[:, t]] + emissions[idx, t, tags[:, t]]
            score = score + step * mask[:, t]
        last = tags[idx, mask.sum(1) - 1]
        return score + self.end_transitions[last]

    def _log_partition(self, emissions, mask, trans, start):
        alpha = start + emissions[:, 0]                                  # (B,K)
        for t in range(1, emissions.shape[1]):
            nxt = torch.logsumexp(alpha.unsqueeze(2) + trans + emissions[:, t].unsqueeze(1), dim=1)
            alpha = torch.where(mask[:, t].unsqueeze(1), nxt, alpha)
        return torch.logsumexp(alpha + self.end_transitions, dim=1)

    @torch.no_grad()
    def marginals(self, emissions: torch.Tensor, mask: torch.Tensor) -> torch.Tensor:
        """P(tag_t = k | sentence) for every position (forward-backward), shape (B,T,K).

        These are the calibrated per-word probabilities under the full CRF (emissions +
        transitions), used as entity confidence. Padded positions are meaningless."""
        mask = mask.bool()
        trans, start = self._masked_params()
        batch, seq_len, k = emissions.shape
        alphas = [start + emissions[:, 0]]
        for t in range(1, seq_len):
            nxt = torch.logsumexp(alphas[-1].unsqueeze(2) + trans + emissions[:, t].unsqueeze(1), dim=1)
            alphas.append(torch.where(mask[:, t].unsqueeze(1), nxt, alphas[-1]))
        lengths = mask.sum(1)
        betas = [None] * seq_len
        beta = self.end_transitions.expand(batch, k)
        for t in range(seq_len - 1, -1, -1):
            # Positions at or past each sequence's last word start from the end transitions.
            is_last_or_pad = (t >= lengths - 1).unsqueeze(1)
            if t < seq_len - 1:
                step = torch.logsumexp(trans.unsqueeze(0) + (emissions[:, t + 1] + betas[t + 1]).unsqueeze(1), dim=2)
                beta = torch.where(is_last_or_pad, self.end_transitions.expand(batch, k), step)
            betas[t] = beta
        alpha = torch.stack(alphas, 1)
        beta = torch.stack(betas, 1)
        log_z = torch.logsumexp(alphas[-1] + self.end_transitions, dim=1)
        return (alpha + beta - log_z.view(batch, 1, 1)).exp()

    @torch.no_grad()
    def decode(self, emissions: torch.Tensor, mask: torch.Tensor) -> list[list[int]]:
        """Viterbi: the highest-scoring valid tag sequence for each item (unpadded lengths)."""
        mask = mask.bool()
        trans, start = self._masked_params()
        score = start + emissions[:, 0]
        history = []
        for t in range(1, emissions.shape[1]):
            best, idx = (score.unsqueeze(2) + trans).max(dim=1)
            nxt = best + emissions[:, t]
            score = torch.where(mask[:, t].unsqueeze(1), nxt, score)
            history.append(idx)
        score = score + self.end_transitions
        lengths = mask.sum(1).tolist()
        paths = []
        for b, length in enumerate(lengths):
            best_last = int(score[b].argmax())
            path = [best_last]
            for t in range(length - 2, -1, -1):
                path.append(int(history[t][b, path[-1]]))
            paths.append(path[::-1])
        return paths
