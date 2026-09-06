"""Unit tests covering all public functions in jellyfin-plugin-contentfilter."""

import json
from pathlib import Path
from unittest.mock import MagicMock, patch
import sqlite3

import deploy_got_jcfs as dgot
import generate_got_summary as ggot
import download_bmc_posts as dbmc
import process_to_jcf as pjcf
import tools.shift_cues as sc
import tools.shift_srt as ss
import tools.reddit_scraper as rs
import tools.reddit_to_jcf as rj
import tools.jcf_db as jdb


def test_process_to_jcf_is_header_line():
    assert pjcf.is_header_line("SEX AND NUDITY") is True
    assert pjcf.is_header_line("00:01:23 - 00:02:00") is False
    assert pjcf.is_header_line("") is False


def test_process_to_jcf_run(tmp_path: Path):
    input_file = tmp_path / "input.json"
    output_dir = tmp_path / "output"
    posts = [
        {
            "title": "Test Movie (2020)",
            "media_title": "Test Movie",
            "year": 2020,
            "plain_text": "00:01:00 - 00:01:10 swearing\n",
        }
    ]
    input_file.write_text(json.dumps(posts), encoding="utf-8")

    proc = pjcf.JcfProcessor(input_file=input_file, output_dir=output_dir)
    res = proc.run()
    assert res["total_jcf_files_written"] == 1
    assert res["total_cues_generated"] == 1


def test_deploy_got_jcfs_parse_and_format():
    content = (
        "00:01:00.000 --> 00:01:10.000\n"
        "category: Violence.Moderate\n"
        "channel: video\n"
        "action: skip\n"
        "description: fight scene\n\n"
    )
    cues = dgot.parse_jcf_cues(content)
    assert len(cues) == 1
    assert cues[0]["start"] == "00:01:00.000"

    formatted = dgot.format_cues_jcf("GoT", "2011", "CleanStream", cues)
    assert "00:01:00.000 --> 00:01:10.000" in formatted
    assert "category: Violence.Moderate" in formatted


def test_deploy_got_jcfs_deploy(tmp_path: Path):
    with patch("deploy_got_jcfs.MEDIA_DIR", tmp_path):
        res = dgot.deploy(dry_run=True)
        assert res is True


def test_generate_got_summary_helpers(tmp_path: Path):
    assert ggot.ts_to_seconds("01:02:03") == 3723.0
    assert ggot.fmt_duration(65) == "1m 05s"
    assert ggot.fmt_duration(3665) == "61m 05s"

    jcf_file = tmp_path / "sample.jcf"
    jcf_file.write_text(
        "WEBVTT JCF\n\nNOTE\nTITLE Sample\n\n00:01:00.000 --> 00:01:30.000\ncategory: Violence\nchannel: video\naction: skip\n\n",
        encoding="utf-8",
    )
    header, cues = ggot.parse_jcf_file(jcf_file)
    assert header is not None
    assert len(cues) == 1
    assert cues[0]["duration_sec"] == 30.0


def test_download_bmc_posts_client(tmp_path: Path):
    client = dbmc.BuyMeACoffeeClient(output_dir=tmp_path)
    client.session = MagicMock()

    # save_session
    with patch("pickle.dump"):
        client.save_session([{"name": "session_cookie", "value": "xyz"}])

    # login_browser
    with patch.object(client, "_create_chrome_driver") as mock_create, \
         patch.object(client, "_submit_credentials_and_otp"):
        mock_driver = MagicMock()
        mock_driver.get_cookies.return_value = [{"name": "test", "value": "val"}]
        mock_create.return_value = mock_driver
        sess = client.login_browser(email="test@test.com", password="pwd", headless=True)
        assert sess is not None

    # fetch_all_posts
    with patch.object(client, "_fetch_main_feed", side_effect=lambda all_posts, per_page: all_posts.update({1: {"id": 1}})), \
         patch.object(client, "_fetch_category_feed"):
        posts = client.fetch_all_posts()
        assert len(posts) == 1

    # process_and_export
    raw_posts = [
        {
            "id": 101,
            "title": "Movie Test (2021)",
            "content": "Description with 00:01:00 - 00:01:10 nudity",
            "slug": "movie-test-2021",
        }
    ]
    with patch.object(client, "_write_single_markdown_file"):
        summary = client.process_and_export(raw_posts)
        assert summary["total_posts"] == 1


def test_tools_shift_cues_methods(tmp_path: Path):
    cue = sc.JcfCue(
        start_ms=1000,
        end_ms=5000,
        category="Violence.Moderate",
        channel="video",
        action="skip",
    )
    assert cue.matches_channel("video") is True
    assert cue.matches_channel("audio") is False

    jcf_content = (
        "WEBVTT JCF\n\nNOTE\nTITLE Test\n\n00:01:00.000 --> 00:01:10.000\n"
        "category: Violence.Moderate\nchannel: video\naction: skip\n\n"
    )
    headers, cues = sc.parse_jcf_cues(jcf_content)
    assert len(cues) == 1
    assert cues[0].start_ms == 60000

    jcf_file = tmp_path / "shift_test.jcf"
    jcf_file.write_text(jcf_content, encoding="utf-8")
    mod = sc.process_file(jcf_file, offset_seconds=1.0, channel="all", inplace=True)
    assert mod is True


def test_tools_shift_srt_process_srt_file(tmp_path: Path):
    srt_content = "1\n00:00:10,000 --> 00:00:15,000\nHello world!\n\n"
    in_file = tmp_path / "in.srt"
    out_file = tmp_path / "out.srt"
    in_file.write_text(srt_content, encoding="utf-8")

    res = ss.process_srt_file(in_file, offset_seconds=1.0, output_path=out_file)
    assert res is True
    assert out_file.exists()


def test_tools_reddit_scraper_functions(tmp_path: Path):
    with patch("urllib.request.urlopen") as mock_urlopen:
        mock_resp = MagicMock()
        mock_resp.read.return_value = json.dumps({"data": [{"title": "Matrix"}]}).encode("utf-8")
        mock_urlopen.return_value.__enter__.return_value = mock_resp

        with patch("tools.reddit_scraper.RAW_DIR", tmp_path):
            with patch("json.load", return_value=[{"title": "Seed 1"}]):
                count = rs.download_cleanstream_seed()
                assert count == 1

            dudes_src = tmp_path / "posts_clean.json"
            dudes_src.write_text(json.dumps([{"title": "Film"}]), encoding="utf-8")
            with patch("tools.reddit_scraper.Path") as mock_path_cls:
                mock_path_cls.side_effect = lambda *args: dudes_src if "thetimestampdudes_posts" in str(args[0]) else Path(*args)
                rs.sync_thetimestampdudes_clean()

            curated_count = rs.save_curated_reddit_posts()
            assert curated_count > 0

            with patch("time.sleep"):
                results = rs.search_reddit_pullpush("Matrix", "cleanstream", size=5)
                assert len(results) == 1


def test_tools_reddit_to_jcf_to_jcf():
    doc = rj.JcfDocument(
        title="Gladiator",
        year="2000",
        cues=[
            rj.ParsedCue(
                start_ms=1000,
                end_ms=5000,
                start_str="00:00:01.000",
                end_str="00:00:05.000",
                category="Violence.Moderate",
                channel="video",
                action="skip",
            )
        ],
    )
    text = doc.to_jcf()
    assert "TITLE Gladiator" in text
    assert "00:00:01.000 --> 00:00:05.000" in text


def test_tools_jcf_db_operations(tmp_path: Path):
    conn = jdb.init_sqlite(":memory:")
    assert isinstance(conn, sqlite3.Connection)

    doc = rj.JcfDocument(
        title="Sample Movie",
        year="2022",
        imdb_id="tt1234567",
        source="Test",
        cues=[
            rj.ParsedCue(
                start_ms=10000,
                end_ms=20000,
                start_str="00:00:10.000",
                end_str="00:00:20.000",
                category="Violence.Moderate",
                channel="video",
                action="skip",
                description="Action scene",
            )
        ],
    )
    out_file = tmp_path / "Sample Movie (2022).jcf"
    assert jdb.save_jcf_and_index(conn, doc, out_file) is True

    with patch("tools.jcf_db.CATALOG_JSON", tmp_path / "catalog.json"):
        jdb.export_catalog_json(conn)
        assert (tmp_path / "catalog.json").exists()

    with patch("tools.jcf_db.CATALOG_DB", tmp_path / "test.db"), \
         patch("sqlite3.connect", return_value=conn):
        jdb.cmd_search("Sample")
        jdb.cmd_stats()
        jdb.cmd_export(str(tmp_path / "export"))
        jdb.cmd_upgrade(str(tmp_path))


def test_tools_jcf_db_builders(tmp_path: Path):
    conn = jdb.init_sqlite(":memory:")

    with patch("tools.jcf_db.RAW_DIR", tmp_path), patch("tools.jcf_db.MOVIES_DIR", tmp_path / "movies"):
        (tmp_path / "movies").mkdir(parents=True, exist_ok=True)
        cleanstream_json = tmp_path / "cleanstream_seed.json"
        cleanstream_json.write_text(json.dumps([{"title": "Clean Movie", "year": 2021, "cues": []}]), encoding="utf-8")
        assert jdb.build_from_cleanstream(conn) >= 0

        dudes_json = tmp_path / "thetimestampdudes_clean.json"
        dudes_json.write_text(json.dumps([{"title": "Dudes Movie", "year": 2022, "plain_text": "00:01:00 - 00:02:00 fight"}]), encoding="utf-8")
        assert jdb.build_from_thetimestampdudes(conn) >= 0

        reddit_json = tmp_path / "reddit_curated_posts.json"
        reddit_json.write_text(json.dumps([{"title": "Reddit Movie", "year": 2023, "body": "00:03:00 - 00:04:00 nudity"}]), encoding="utf-8")
        assert jdb.build_from_curated_reddit(conn) >= 0

        with patch("got_to_jcf.EPISODES", {"S01E01": "00:01:00 - 00:02:00"}):
            assert jdb.build_from_game_of_thrones(conn) >= 0

    with patch("tools.jcf_db.init_sqlite", return_value=conn), \
         patch("tools.jcf_db.build_from_cleanstream"), \
         patch("tools.jcf_db.build_from_thetimestampdudes"), \
         patch("tools.jcf_db.build_from_curated_reddit"), \
         patch("tools.jcf_db.build_from_game_of_thrones"), \
         patch("tools.jcf_db.export_catalog_json"), \
         patch("tools.jcf_db.cmd_stats"):
        jdb.cmd_build()
