import re
import pytest

def mask_leaving_first_letter(word: str) -> str:
    if not word:
        return word
    return re.sub(r"\b\w+", lambda m: m.group(0)[0] + "*" * (len(m.group(0)) - 1) if len(m.group(0)) > 1 else m.group(0), word)

def redact_phrases(text: str, phrases: list[str]) -> str:
    if not text:
        return text
    output = text
    # Sort phrases longest first
    sorted_phrases = sorted([p.strip() for p in phrases if p.strip()], key=len, reverse=True)
    for p in sorted_phrases:
        pattern = rf"\b{re.escape(p)}\b"
        output = re.sub(pattern, lambda m: mask_leaving_first_letter(m.group(0)), output, flags=re.IGNORECASE)
    return output

def test_mask_leaving_first_letter():
    assert mask_leaving_first_letter("bastard") == "b******"
    assert mask_leaving_first_letter("Bastard") == "B******"
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

def test_redact_phrases_punctuation_and_casing():
    dialogue = "Bastard! You bloody idiot... DAMN IT!"
    phrases = ["bastard", "bloody", "damn"]
    redacted = redact_phrases(dialogue, phrases)
    assert redacted == "B******! You b***** idiot... D*** IT!"

def test_redact_srt_block():
    srt_block = """1
00:01:20,100 --> 00:01:23,400
The bastard has no honor!

2
00:01:24,000 --> 00:01:26,000
None at all, bloody shame.
"""
    redacted = redact_phrases(srt_block, ["bastard", "bloody"])
    assert "The b****** has no honor!" in redacted
    assert "None at all, b***** shame." in redacted
