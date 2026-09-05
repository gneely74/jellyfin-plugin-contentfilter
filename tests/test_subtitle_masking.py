import re
import pytest

def mask_leaving_first_letter(word: str) -> str:
    if not word:
        return word
    return re.sub(r"\b\w+", lambda m: m.group(0)[0] + "*" * (len(m.group(0)) - 1) if len(m.group(0)) > 1 else m.group(0), word)

def build_word_pattern(term: str) -> str:
    if not term or not term.strip():
        return ""
    term = term.strip()
    if " " in term:
        return rf"\b{re.escape(term)}\b"
    escaped = re.escape(term)
    if len(term) > 2 and term.lower().endswith("y") and term[-2].lower() not in "aeiou":
        stem = re.escape(term[:-1])
        return rf"\b(?:{stem}ies|{escaped})\b"
    if term.lower().endswith("ss"):
        return rf"\b(?:{escaped}es|{escaped})\b"
    if term.lower().endswith("s"):
        return rf"\b{escaped}\b"
    if any(term.lower().endswith(suffix) for suffix in ("sh", "ch", "x", "z")):
        return rf"\b(?:{escaped}es|{escaped})\b"
    return rf"\b(?:{escaped}s|{escaped})\b"

def redact_phrases(text: str, phrases: list[str]) -> str:
    if not text:
        return text
    output = text
    # Sort phrases longest first
    sorted_phrases = sorted([p.strip() for p in phrases if p.strip()], key=len, reverse=True)
    for p in sorted_phrases:
        pattern = build_word_pattern(p)
        if not pattern:
            continue
        output = re.sub(pattern, lambda m: mask_leaving_first_letter(m.group(0)), output, flags=re.IGNORECASE)
    return output

def test_mask_leaving_first_letter():
    assert mask_leaving_first_letter("bastard") == "b******"
    assert mask_leaving_first_letter("Bastard") == "B******"
    assert mask_leaving_first_letter("bastards") == "b*******"
    assert mask_leaving_first_letter("bitches") == "b******"
    assert mask_leaving_first_letter("assholes") == "a*******"
    assert mask_leaving_first_letter("damn") == "d***"
    assert mask_leaving_first_letter("DAMN") == "D***"
    assert mask_leaving_first_letter("hell") == "h***"
    assert mask_leaving_first_letter("piss") == "p***"
    assert mask_leaving_first_letter("bloody") == "b*****"
    assert mask_leaving_first_letter("a") == "a"
    assert mask_leaving_first_letter("") == ""

def test_mask_phrase():
    assert mask_leaving_first_letter("son of a bitch") == "s** o* a b****"

def test_redact_phrases_in_dialogue():
    dialogue = "Look at that bastard over there! Damn right, he is a bloody fool."
    phrases = ["bastard", "damn", "bloody"]
    redacted = redact_phrases(dialogue, phrases)
    assert redacted == "Look at that b****** over there! D*** right, he is a b***** fool."

def test_redact_plurals_in_dialogue():
    dialogue = "Those bastards and bitches are complete assholes!"
    # Notice we supply singular forms: bastard, bitch, asshole
    phrases = ["bastard", "bitch", "asshole"]
    redacted = redact_phrases(dialogue, phrases)
    assert redacted == "Those b******* and b****** are complete a*******!"

def test_redact_phrases_punctuation_and_casing():
    dialogue = "Bastard! You bloody idiot... DAMN IT!"
    phrases = ["bastard", "bloody", "damn"]
    redacted = redact_phrases(dialogue, phrases)
    assert redacted == "B******! You b***** idiot... D*** IT!"

def test_redact_srt_block():
    srt_block = """1
00:01:20,100 --> 00:01:23,400
The bastards have no honor!

2
00:01:24,000 --> 00:01:26,000
None at all, bloody shame.
"""
    redacted = redact_phrases(srt_block, ["bastard", "bloody"])
    assert "The b******* have no honor!" in redacted
    assert "None at all, b***** shame." in redacted
