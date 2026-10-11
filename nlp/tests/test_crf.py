"""The CRF is checked against brute-force enumeration of every tag sequence."""
import itertools

import torch

from jobmatch_nlp.annotation import BIO_TAGS
from jobmatch_nlp.crf import CRF, bio_allowed_transitions


def brute_force(crf: CRF, emissions: torch.Tensor, length: int):
    trans, start = crf._masked_params()
    scores = {}
    for seq in itertools.product(range(crf.num_tags), repeat=length):
        s = start[seq[0]] + emissions[0, seq[0]]
        for t in range(1, length):
            s = s + trans[seq[t - 1], seq[t]] + emissions[t, seq[t]]
        scores[seq] = s + crf.end_transitions[seq[-1]]
    return scores


def test_nll_and_viterbi_match_brute_force_with_padding():
    torch.manual_seed(0)
    crf = CRF(3)
    emissions = torch.randn(2, 4, 3)
    mask = torch.tensor([[1, 1, 1, 1], [1, 1, 0, 0]], dtype=torch.bool)
    tags = torch.tensor([[0, 2, 1, 1], [2, 0, 0, 0]])

    for b, length in enumerate([4, 2]):
        scores = brute_force(crf, emissions[b], length)
        log_z = torch.logsumexp(torch.stack(list(scores.values())), 0)
        gold = tuple(tags[b, :length].tolist())
        expected_nll = log_z - scores[gold]
        nll = crf(emissions[b:b + 1], tags[b:b + 1], mask[b:b + 1])
        assert torch.allclose(nll, expected_nll, atol=1e-4)
        best = max(scores, key=lambda k: scores[k])
        assert crf.decode(emissions[b:b + 1], mask[b:b + 1])[0] == list(best)


def test_marginals_match_brute_force_with_padding():
    torch.manual_seed(2)
    allowed, start = bio_allowed_transitions(("O", "B-X", "I-X"))
    crf = CRF(3, allowed, start)
    emissions = torch.randn(2, 4, 3)
    mask = torch.tensor([[1, 1, 1, 1], [1, 1, 1, 0]], dtype=torch.bool)
    marg = crf.marginals(emissions, mask)

    for b, length in enumerate([4, 3]):
        scores = brute_force(crf, emissions[b], length)
        probs = torch.softmax(torch.stack(list(scores.values())), 0)
        expected = torch.zeros(length, 3)
        for p, seq in zip(probs, scores):
            for t, tag in enumerate(seq):
                expected[t, tag] += p
        assert torch.allclose(marg[b, :length], expected, atol=1e-4)
        assert torch.allclose(marg[b, :length].sum(-1), torch.ones(length), atol=1e-4)


def test_decode_never_produces_invalid_bio():
    torch.manual_seed(1)
    allowed, start = bio_allowed_transitions(BIO_TAGS)
    crf = CRF(len(BIO_TAGS), allowed, start)
    emissions = torch.randn(32, 8, len(BIO_TAGS)) * 3
    mask = torch.ones(32, 8, dtype=torch.bool)
    for path in crf.decode(emissions, mask):
        tags = [BIO_TAGS[i] for i in path]
        prev = "O"
        for tag in tags:
            if tag.startswith("I-"):
                assert prev != "O" and prev[2:] == tag[2:], tags
            prev = tag


def test_allowed_transitions():
    allowed, start = bio_allowed_transitions(("O", "B-X", "I-X", "B-Y", "I-Y"))
    assert allowed[1, 2] and allowed[2, 2]          # B-X -> I-X, I-X -> I-X
    assert not allowed[0, 2] and not allowed[3, 2]  # O -> I-X, B-Y -> I-X
    assert start.tolist() == [True, True, False, True, False]
