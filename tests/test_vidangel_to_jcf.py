"""Tests for the VidAngel to Jellyfin Content Filter (JCF) converter tool."""

from __future__ import annotations

import json
from unittest.mock import MagicMock, patch
import pytest

from tests.test_jcf_database import VALID_CATEGORIES
from tools.vidangel_to_jcf import (
    JcfDocument,
    ParsedCue,
    VidAngelClient,
    VidAngelTag,
    convert_vidangel_tags_to_cues,
    map_vidangel_tag,
    ms_to_timestamp,
    select_best_offering,
)


def test_ms_to_timestamp() -> None:
    """Verifies that milliseconds are correctly converted into WebVTT timestamp strings."""
    assert ms_to_timestamp(0) == "00:00:00.000"
    assert ms_to_timestamp(1500) == "00:00:01.500"
    assert ms_to_timestamp(65432) == "00:01:05.432"
    assert ms_to_timestamp(3661005) == "01:01:01.005"
    # Negative values should clamp to 0
    assert ms_to_timestamp(-500) == "00:00:00.000"


def test_map_vidangel_primary_categories() -> None:
    """Verifies that each primary VidAngel category identifier maps to a recognized category."""
    test_cases = [
        (18, "Profanity", "audio", "Language.GeneralProfanity", "audio", "mute"),
        (20, "Blasphemy", "audio", "Language.Blasphemy", "audio", "mute"),
        (23, "Racial slur", "audio", "Language.RacialAndBigotedSlurs", "audio", "mute"),
        (22, "Sex word", "audio", "SexualReferences.ExplicitWords", "audio", "mute"),
        (22, "Dirty talk", "audiovisual", "SexualReferences.ContextualDialogue", "video", "skip"),
        (2, "Bikini scene", "audiovisual", "SexAndNudity.PartialNudity", "video", "skip"),
        (6, "Intercourse", "audiovisual", "SexAndNudity.Graphic", "video", "skip"),
        (9, "Passionate kiss", "audiovisual", "SexAndNudity.PhysicalIntimacy", "video", "skip"),
        (14, "Fistfight", "audiovisual", "Violence.Moderate", "video", "skip"),
        (15, "Graphic stabbing", "audiovisual", "Violence.Graphic", "video", "skip"),
        (16, "Decapitation", "audiovisual", "Violence.Gore", "video", "skip"),
        (4, "Beer drinking", "audiovisual", "Substances.Alcohol", "video", "skip"),
        (4, "Cigarette smoke", "audiovisual", "Substances.Tobacco", "video", "skip"),
        (4, "Cocaine snorting", "audiovisual", "Substances.IllegalDrugs", "video", "skip"),
        (27, "Surgery operation", "audiovisual", "Medical.Events", "both", "skip"),
        (27, "Vomiting", "audiovisual", "Medical.BodilyFunctions", "both", "skip"),
        (24, "Closing Credits", "audiovisual", "Structural.Credits", "both", "skip"),
    ]

    for cat_id, desc, tag_type, exp_cat, exp_chan, exp_act in test_cases:
        tag = VidAngelTag(
            id=1,
            tag_set_id=1,
            category_id=cat_id,
            description=desc,
            tag_type=tag_type,
            start_approx=10,
            end_approx=20,
        )
        cat, chan, act = map_vidangel_tag(tag)
        assert cat == exp_cat, f"Expected {exp_cat} for category_id {cat_id} ('{desc}'), got {cat}"
        assert chan == exp_chan
        assert act == exp_act
        assert cat in VALID_CATEGORIES


def test_convert_vidangel_tags_to_cues_with_offset() -> None:
    """Verifies that tag conversion accurately shifts timestamps with offset and handles durations."""
    tags = [
        VidAngelTag(
            id=101,
            tag_set_id=1,
            category_id=18,
            description="sh*t",
            tag_type="audio",
            start_approx=10,
            end_approx=10,  # Zero-duration point marker
        ),
        VidAngelTag(
            id=102,
            tag_set_id=1,
            category_id=14,
            description="Combat scene",
            tag_type="audiovisual",
            start_approx=30,
            end_approx=45,
        ),
    ]

    # Positive 5.5s offset with 1.0s minimum duration padding
    cues = convert_vidangel_tags_to_cues(tags, time_offset_seconds=5.5, min_cue_duration_seconds=1.0)
    assert len(cues) == 2

    # First cue: 10 + 5.5 = 15.5s (15500 ms) -> padded to 16.5s (16500 ms)
    assert cues[0].start_ms == 15500
    assert cues[0].end_ms == 16500
    assert cues[0].start_str == "00:00:15.500"
    assert cues[0].end_str == "00:00:16.500"
    assert cues[0].category == "Language.GeneralProfanity"
    assert cues[0].action == "mute"

    # Second cue: 30 + 5.5 = 35.5s -> 45 + 5.5 = 50.5s
    assert cues[1].start_ms == 35500
    assert cues[1].end_ms == 50500
    assert cues[1].start_str == "00:00:35.500"
    assert cues[1].end_str == "00:00:50.500"
    assert cues[1].category == "Violence.Moderate"
    assert cues[1].action == "skip"


def test_convert_vidangel_tags_negative_offset_clamping() -> None:
    """Verifies that negative offsets clamp start time at 00:00:00.000 without negative timestamps."""
    tags = [
        VidAngelTag(
            id=201,
            tag_set_id=1,
            category_id=33,
            description="Opening Credits",
            tag_type="audiovisual",
            start_approx=5,
            end_approx=20,
        )
    ]

    # Negative 10 second offset should clamp start to 0ms and end to 10s (10000ms)
    cues = convert_vidangel_tags_to_cues(tags, time_offset_seconds=-10.0)
    assert len(cues) == 1
    assert cues[0].start_ms == 0
    assert cues[0].end_ms == 10000
    assert cues[0].start_str == "00:00:00.000"
    assert cues[0].end_str == "00:00:10.000"


def test_convert_vidangel_tags_credits_exclusion() -> None:
    """Verifies that opening and closing credit cues can be excluded when requested."""
    tags = [
        VidAngelTag(
            id=301,
            tag_set_id=1,
            category_id=24,
            description="Opening Credits",
            tag_type="audiovisual",
            start_approx=0,
            end_approx=60,
        ),
        VidAngelTag(
            id=302,
            tag_set_id=1,
            category_id=18,
            description="d*mn",
            tag_type="audio",
            start_approx=120,
            end_approx=120,
        ),
    ]

    cues = convert_vidangel_tags_to_cues(tags, include_credits=False)
    assert len(cues) == 1
    assert cues[0].category == "Language.GeneralProfanity"


def test_jcf_document_serialization() -> None:
    """Verifies that JcfDocument formats standard WEBVTT JCF text matching the specification."""
    cues = [
        ParsedCue(
            start_ms=10000,
            end_ms=15000,
            start_str="00:00:10.000",
            end_str="00:00:15.000",
            category="Violence.Moderate",
            channel="video",
            action="skip",
            description="Sword duel\nwith two enemies",
        )
    ]

    doc = JcfDocument(
        title="Sample Movie",
        year="2024",
        imdb_id="tt9999999",
        source="https://www.vidangel.com/",
        cues=cues,
    )

    content = doc.to_jcf()
    assert content.startswith("WEBVTT JCF\n")
    assert "NOTE\nTITLE Sample Movie\nYEAR 2024\nIMDB tt9999999\nSOURCE https://www.vidangel.com/" in content
    assert "00:00:10.000 --> 00:00:15.000\ncategory: Violence.Moderate\nchannel: video\naction: skip\ndescription: Sword duel with two enemies" in content


def test_select_best_offering() -> None:
    """Verifies that platform preferences and default offerings are selected correctly."""
    offerings = [
        {"catalog_id": "paramount", "type": "RENTAL", "tag_set_id": 101, "watching_with": "Paramount"},
        {"catalog_id": "amazon", "type": "PURCHASE", "tag_set_id": 102, "watching_with": "Amazon Buy"},
        {"catalog_id": "netflix", "type": "SUBSCRIPTION", "tag_set_id": 103, "watching_with": "Netflix"},
    ]

    # Preferred platform selection
    selected_amz = select_best_offering(offerings, preferred_platform="amazon")
    assert selected_amz["tag_set_id"] == 102

    selected_par = select_best_offering(offerings, preferred_platform="paramount")
    assert selected_par["tag_set_id"] == 101

    # Default fallback should favor SUBSCRIPTION
    selected_sub = select_best_offering(offerings)
    assert selected_sub["tag_set_id"] == 103

    # Error when no offerings have tag_set_id
    with pytest.raises(ValueError, match="No offerings found"):
        select_best_offering([{"catalog_id": "empty"}])


@patch("urllib.request.urlopen")
def test_vidangel_client_search_and_fetch(mock_urlopen: MagicMock) -> None:
    """Verifies client methods with mocked HTTP responses."""
    client = VidAngelClient()

    # 1. Test search
    mock_resp = MagicMock()
    mock_resp.read.return_value = json.dumps({
        "titles": [{"id": 10, "title": "Test Movie", "slug": "test-movie-123", "type": "movie", "tag_count": 50}]
    }).encode("utf-8")
    mock_urlopen.return_value.__enter__.return_value = mock_resp

    results = client.search("Test")
    assert len(results) == 1
    assert results[0]["title"] == "Test Movie"
    assert results[0]["slug"] == "test-movie-123"

    # 2. Test get_tag_set
    mock_resp.read.return_value = json.dumps({
        "tags": [
            {
                "id": 500,
                "tag_set_id": 999,
                "category_id": 18,
                "description": "b*st*rd",
                "type": "audio",
                "start_approx": 100,
                "end_approx": 102,
            }
        ]
    }).encode("utf-8")

    tags = client.get_tag_set(999)
    assert len(tags) == 1
    assert tags[0].id == 500
    assert tags[0].start_approx == 100
    assert tags[0].end_approx == 102
    assert tags[0].description == "b*st*rd"
