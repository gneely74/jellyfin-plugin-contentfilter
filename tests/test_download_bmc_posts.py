"""Unit tests for download_bmc_posts."""

import pytest
from download_bmc_posts import (
    TITLE_REGEX,
    CleanPost,
    clean_html_to_text,
    sanitize_filename,
)


def test_title_regex_movie_with_year():
    m = TITLE_REGEX.match("Timestamps to skip in Do The Right Thing (1989)")
    assert m is not None
    assert m.group(1).strip() == "Do The Right Thing"
    assert m.group(2) == "1989"


def test_title_regex_complex_title():
    m = TITLE_REGEX.match(
        "Timestamps to skip in The Lord of the Rings: The Fellowship of the Ring (Extended) (2001)"
    )
    assert m is not None
    assert (
        m.group(1).strip()
        == "The Lord of the Rings: The Fellowship of the Ring (Extended)"
    )
    assert m.group(2) == "2001"


def test_title_regex_non_timestamp_post():
    m = TITLE_REGEX.match("FAQ For Our Page! 💬")
    assert m is not None
    assert m.group(1).strip() == "FAQ For Our Page! 💬"
    assert m.group(2) is None


def test_clean_html_to_text():
    raw_html = (
        "<p>This movie contains strong language.</p>"
        "<p><strong>VIOLENCE</strong></p>"
        "<p>12:30 - 14:00 Fight scene in bar.</p>"
    )
    text = clean_html_to_text(raw_html)
    assert "This movie contains strong language." in text
    assert "VIOLENCE" in text
    assert "12:30 - 14:00 Fight scene in bar." in text
    assert "<p>" not in text
    assert "<strong>" not in text


def test_sanitize_filename():
    assert sanitize_filename("Ace Ventura: Pet Detective") == "Ace Ventura_ Pet Detective"
    assert sanitize_filename("What / If * Query?") == "What _ If _ Query"
    assert sanitize_filename("  Normal Movie (1999)  ") == "Normal Movie (1999)"


def test_clean_post_creation():
    post = CleanPost(
        id=12345,
        title="Timestamps to skip in The Matrix (1999)",
        media_title="The Matrix",
        year=1999,
        slug="timestamps-to-skip-the-matrix-1999",
        published_on="2026-01-01T00:00:00Z",
        is_unlocked=True,
        is_pinned=False,
        tags=["Sci-Fi", "Action"],
        plain_text="00:10:00 - 00:12:00 Action scene",
        raw_html="<p>00:10:00 - 00:12:00 Action scene</p>",
        char_count=33,
    )
    assert post.id == 12345
    assert post.media_title == "The Matrix"
    assert post.year == 1999
    assert post.is_unlocked is True


def test_load_dotenv(tmp_path, monkeypatch):
    import os
    from download_bmc_posts import load_dotenv

    env_file = tmp_path / ".env"
    env_file.write_text("BMC_TEST_EMAIL=test@example.com\nBMC_TEST_PASS=secret123\n# Comment\n")

    monkeypatch.delenv("BMC_TEST_EMAIL", raising=False)
    monkeypatch.delenv("BMC_TEST_PASS", raising=False)

    load_dotenv(env_file)

    assert os.environ.get("BMC_TEST_EMAIL") == "test@example.com"
    assert os.environ.get("BMC_TEST_PASS") == "secret123"

