#!/usr/bin/env python3
"""Buy Me a Coffee (BMC) Post Downloader for TheTimestampDudes.

This utility authenticates with Buy Me a Coffee (via saved session cookies or
browser-assisted login with OTP support) and downloads all posts published by
a creator (default: thetimestampdudes), including full unlocked content and timestamps
to prepare for Jellyfin Content Filter (JCF) cue creation.
"""

from __future__ import annotations

import argparse
import html
import json
import os
import re
import sys
import time
import urllib.parse
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional

try:
    import requests
except ImportError:
    print("Error: 'requests' is required. Run: pip install requests", file=sys.stderr)
    sys.exit(1)

try:
    from bs4 import BeautifulSoup
except ImportError:
    BeautifulSoup = None  # Fallback to regex cleaning if bs4 is missing


BASE_APP_URL = "https://app.buymeacoffee.com"
BASE_WEB_URL = "https://buymeacoffee.com"
DEFAULT_CREATOR = "thetimestampdudes"
DEFAULT_SESSION_FILE = "bmc_session.json"
DEFAULT_OUTPUT_DIR = "thetimestampdudes_posts"

TITLE_REGEX = re.compile(
    r"^(?:Timestamps\s+to\s+skip\s+in\s+)?(.+?)(?:\s*\((\d{4})\))?\s*$",
    re.IGNORECASE,
)


@dataclass
class CleanPost:
    id: int
    title: str
    media_title: str
    year: Optional[int]
    slug: str
    published_on: str
    is_unlocked: bool
    is_pinned: bool
    tags: List[str]
    plain_text: str
    raw_html: str
    char_count: int


def clean_html_to_text(raw_html: str) -> str:
    """Convert HTML post description into clean readable plain text with preserved lines."""
    if not raw_html:
        return ""

    if BeautifulSoup:
        soup = BeautifulSoup(raw_html, "html.parser")
        for br in soup.find_all(["br", "p", "div", "li"]):
            br.append("\n")
        text = soup.get_text()
    else:
        # Fallback simple regex
        text = re.sub(r"<(?:br|p|div|li)[^>]*>", "\n", raw_html, flags=re.IGNORECASE)
        text = re.sub(r"<[^>]+>", "", text)

    text = html.unescape(text)
    # Normalize multiple newlines and spaces per line
    cleaned_lines = []
    for line in text.splitlines():
        line = line.strip()
        if line:
            cleaned_lines.append(line)
    return "\n".join(cleaned_lines)


def sanitize_filename(name: str) -> str:
    """Sanitize string to be safe for filenames across OSes."""
    # Replace unsafe characters
    s = re.sub(r'[\\/*?:"<>|]', "_", name)
    s = re.sub(r"\s+", " ", s).strip(". _")
    return s or "unnamed_post"


class BuyMeACoffeeClient:
    """Client for Buy Me a Coffee API operations."""

    def __init__(
        self,
        creator: str = DEFAULT_CREATOR,
        session_file: str = DEFAULT_SESSION_FILE,
        output_dir: str = DEFAULT_OUTPUT_DIR,
    ):
        self.creator = creator
        self.session_file = Path(session_file)
        self.output_dir = Path(output_dir)
        self.session = requests.Session()
        self.session.headers.update(
            {
                "User-Agent": (
                    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
                    "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
                ),
                "X-Requested-With": "XMLHttpRequest",
                "Referer": f"{BASE_WEB_URL}/{self.creator}/posts",
                "Accept": "application/json, text/plain, */*",
                "Origin": BASE_WEB_URL,
            }
        )

    def load_session(self) -> bool:
        """Load session cookies from JSON file if present."""
        if not self.session_file.exists():
            return False

        try:
            with open(self.session_file, "r", encoding="utf-8") as f:
                cookies = json.load(f)

            for c in cookies:
                domain = c.get("domain", "")
                if "buymeacoffee" in domain:
                    self.session.cookies.set(
                        c["name"],
                        c["value"],
                        domain=domain,
                        path=c.get("path", "/"),
                    )

            # Set X-XSRF-TOKEN header if present
            xsrf = self.session.cookies.get("XSRF-TOKEN")
            if xsrf:
                self.session.headers["X-XSRF-TOKEN"] = urllib.parse.unquote(xsrf)

            return True
        except Exception as e:
            print(f"[!] Warning: Failed to load session from {self.session_file}: {e}")
            return False

    def save_session(self, cookies: List[Dict[str, Any]]) -> None:
        """Save cookies to the session file."""
        self.session_file.parent.mkdir(parents=True, exist_ok=True)
        with open(self.session_file, "w", encoding="utf-8") as f:
            json.dump(cookies, f, indent=2)
        print(f"[✓] Session saved to {self.session_file}")

    def is_authenticated(self) -> bool:
        """Verify if current session has active unlocked access."""
        url = f"{BASE_APP_URL}/api/v1/posts/creator/{self.creator}?per_page=5"
        try:
            r = self.session.get(url, timeout=10)
            if r.status_code != 200:
                return False
            data = r.json()
            posts = data.get("data", [])
            if not posts:
                return False

            # Check if any member-locked post is unlocked
            # Posts with locked_for > 0 will have is_post_unlocked=True if member authenticated
            for p in posts:
                if p.get("project_update_locked_for", 0) > 0 and p.get("is_post_unlocked"):
                    return True
            # If all were locked_for == 0, check user session cookie
            if self.session.cookies.get("bmc_api_production_session") or self.session.cookies.get(
                "buymeacoffee_session"
            ):
                return True
            return False
        except Exception:
            return False

    def _create_chrome_driver(self, headless: bool):
        from selenium import webdriver
        from selenium.webdriver.chrome.options import Options

        options = Options()
        if headless:
            options.add_argument("--headless=new")
        options.add_argument("--no-sandbox")
        options.add_argument("--disable-dev-shm-usage")
        options.add_argument("--disable-blink-features=AutomationControlled")
        options.add_argument("--window-size=1280,850")
        options.add_argument(
            "user-agent=Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
        )
        print(f"[*] Launching Chrome ({'headless' if headless else 'visible'})...")
        return webdriver.Chrome(options=options)

    def _submit_credentials_and_otp(self, driver, email: str, password: str) -> None:
        from selenium.webdriver.common.by import By
        from selenium.webdriver.common.keys import Keys
        from selenium.webdriver.support import expected_conditions as EC
        from selenium.webdriver.support.ui import WebDriverWait

        print(f"[*] Navigating to {BASE_WEB_URL}/login ...")
        driver.get(f"{BASE_WEB_URL}/login")

        email_input = WebDriverWait(driver, 15).until(
            EC.presence_of_element_located((By.ID, "user_email"))
        )
        email_input.clear()
        email_input.send_keys(email)
        email_input.send_keys(Keys.RETURN)
        time.sleep(2)

        pw_input = WebDriverWait(driver, 15).until(
            EC.visibility_of_element_located((By.ID, "password"))
        )
        pw_input.clear()
        pw_input.send_keys(password)
        pw_input.send_keys(Keys.RETURN)

        print("[*] Credentials submitted. Checking for 2FA verification...")
        time.sleep(4)
        otp_inputs = driver.find_elements(
            By.CSS_SELECTOR, "input[placeholder*='code' i], input#otp"
        )
        if otp_inputs:
            print("\n" + "=" * 50)
            print(f"[!] Buy Me a Coffee sent a login verification code to {email}.")
            otp_code = input(">> Please enter the temporary login code: ").strip()
            print("=" * 50 + "\n")
            otp_inputs[0].clear()
            otp_inputs[0].send_keys(otp_code)
            otp_inputs[0].send_keys(Keys.RETURN)
            time.sleep(5)

    def login_browser(
        self,
        email: str,
        password: str,
        headless: bool = True,
    ) -> bool:
        """Automate browser login via Selenium to obtain session cookies and solve 2FA."""
        try:
            from selenium.webdriver.support.ui import WebDriverWait
        except ImportError:
            print(
                "[!] Selenium is required for browser login. Run: pip install selenium",
                file=sys.stderr,
            )
            return False

        driver = self._create_chrome_driver(headless)
        try:
            self._submit_credentials_and_otp(driver, email, password)
            WebDriverWait(driver, 20).until(
                lambda d: (
                    "buymeacoffee.com/home" in d.current_url
                    or "buymeacoffee.com/dashboard" in d.current_url
                    or "thetimestampdudes" in d.current_url
                )
            )
            driver.get(f"{BASE_WEB_URL}/{self.creator}/posts")
            time.sleep(3)

            cookies = driver.get_cookies()
            self.save_session(cookies)
            self.load_session()
            return True
        except Exception as e:
            print(f"[!] Browser login error: {e}", file=sys.stderr)
            return False
        finally:
            driver.quit()

    def fetch_categories(self) -> List[Dict[str, Any]]:
        """Fetch categories/tags from the creator page."""
        try:
            r = self.session.get(f"{BASE_WEB_URL}/{self.creator}/posts", timeout=15)
            if r.status_code == 200:
                m = re.search(r'data-page="app"[^>]*>(.*?)</script>', r.text, re.DOTALL)
                if m:
                    data = json.loads(m.group(1))
                    return data.get("props", {}).get("categories", [])
        except Exception as e:
            print(f"[!] Warning: Could not fetch categories: {e}")
        return []

    def _fetch_main_feed(self, all_posts: Dict[int, Dict[str, Any]], per_page: int) -> None:
        page = 1
        while True:
            url = f"{BASE_APP_URL}/api/v1/posts/creator/{self.creator}?per_page={per_page}&page={page}"
            resp = self.session.get(url, timeout=15)
            if resp.status_code != 200:
                print(f"[!] HTTP error {resp.status_code} on page {page}", file=sys.stderr)
                break

            data = resp.json()
            items = data.get("data", [])
            if not items:
                break

            for item in items:
                pid = item.get("project_update_id")
                if pid:
                    all_posts[pid] = item

            meta = data.get("meta", {})
            last_page = meta.get("last_page", page)
            total = meta.get("total", len(all_posts))
            print(f"    Page {page}/{last_page} fetched ({len(all_posts)}/{total} posts)")

            if page >= last_page:
                break
            page += 1
            time.sleep(0.2)

    def _fetch_category_feed(self, all_posts: Dict[int, Dict[str, Any]], per_page: int) -> None:
        categories = self.fetch_categories()
        if not categories:
            return
        print(f"[*] Cross-checking {len(categories)} category tags...")
        new_found = 0
        for cat in categories:
            tid = cat.get("tag_id")
            if not tid:
                continue

            cat_page = 1
            while True:
                cat_url = (
                    f"{BASE_APP_URL}/api/v1/posts/creator/{self.creator}"
                    f"?category_id={tid}&per_page={per_page}&page={cat_page}"
                )
                r = self.session.get(cat_url, timeout=15)
                if r.status_code != 200:
                    break
                cdata = r.json()
                citems = cdata.get("data", [])
                if not citems:
                    break

                for item in citems:
                    pid = item.get("project_update_id")
                    if pid and pid not in all_posts:
                        all_posts[pid] = item
                        new_found += 1

                clast = cdata.get("meta", {}).get("last_page", 1)
                if cat_page >= clast:
                    break
                cat_page += 1
                time.sleep(0.1)

        if new_found > 0:
            print(f"    Found {new_found} additional posts across categories!")

    def fetch_all_posts(self) -> List[Dict[str, Any]]:
        """Fetch all posts from the creator's API with pagination and tag cross-referencing."""
        all_posts: Dict[int, Dict[str, Any]] = {}
        per_page = 20

        print(f"[*] Downloading posts for creator '{self.creator}'...")
        self._fetch_main_feed(all_posts, per_page)
        self._fetch_category_feed(all_posts, per_page)

        posts_list = list(all_posts.values())
        posts_list.sort(
            key=lambda p: (
                p.get("project_update_published_on") or p.get("project_update_created_on") or "",
                p.get("project_update_id", 0),
            ),
            reverse=True,
        )
        print(f"[✓] Retrieved a total of {len(posts_list)} unique posts.")
        return posts_list

    def _parse_post_media_info(self, raw_title: str) -> tuple[str, int | None]:
        m = TITLE_REGEX.match(raw_title)
        if m:
            media_title = m.group(1).strip()
            year = int(m.group(2)) if m.group(2) else None
            return media_title, year
        return raw_title, None

    def _build_clean_post_item(self, post: Dict[str, Any]) -> tuple[CleanPost, str, int | None]:
        pid = post.get("project_update_id", 0)
        raw_title = post.get("project_update_heading") or f"Post-{pid}"
        slug = post.get("project_update_slug") or ""
        pub_on = (
            post.get("project_update_published_on")
            or post.get("project_update_created_on")
            or ""
        )
        is_unlocked = bool(post.get("is_post_unlocked"))
        is_pinned = bool(post.get("project_update_is_pinned"))
        raw_desc = post.get("project_update_description") or ""
        plain_text = clean_html_to_text(raw_desc)

        tags_raw = post.get("tags") or []
        tags: List[str] = []
        if isinstance(tags_raw, list):
            for t in tags_raw:
                val = t.get("tag") if isinstance(t, dict) else t
                if val is not None and str(val).strip():
                    tags.append(str(val).strip())

        media_title, year = self._parse_post_media_info(raw_title)
        clean_item = CleanPost(
            id=pid,
            title=raw_title,
            media_title=media_title,
            year=year,
            slug=slug,
            published_on=pub_on,
            is_unlocked=is_unlocked,
            is_pinned=is_pinned,
            tags=tags,
            plain_text=plain_text,
            raw_html=raw_desc,
            char_count=len(plain_text),
        )
        return clean_item, media_title, year

    def _write_single_markdown_file(
        self, md_dir: Path, clean_item: CleanPost, media_title: str, year: int | None
    ) -> None:
        base_fname = (
            f"{sanitize_filename(media_title)} ({year})"
            if year
            else sanitize_filename(media_title)
        )
        md_path = md_dir / f"{base_fname}.md"
        if md_path.exists() and str(clean_item.id) not in md_path.stem:
            md_path = md_dir / f"{base_fname}_{clean_item.id}.md"

        md_content = [
            f"# {clean_item.title}",
            "",
            f"- **Post ID**: `{clean_item.id}`",
            f"- **Media Title**: {media_title}",
            f"- **Year**: {year if year else 'Unknown'}",
            f"- **Published Date**: {clean_item.published_on}",
            f"- **Unlocked**: {'Yes' if clean_item.is_unlocked else 'No'}",
            f"- **Tags**: {', '.join(clean_item.tags) if clean_item.tags else 'None'}",
            f"- **BMC URL**: https://buymeacoffee.com/{self.creator}/{clean_item.slug}",
            "",
            "---",
            "",
            "## Timestamps & Content Descriptions",
            "",
            clean_item.plain_text if clean_item.plain_text else "*(No description or content locked)*",
            "",
        ]
        md_path.write_text("\n".join(md_content), encoding="utf-8")

    def process_and_export(self, raw_posts: List[Dict[str, Any]]) -> Dict[str, Any]:
        """Export raw JSON, clean JSON, individual Markdown files, and summary."""
        self.output_dir.mkdir(parents=True, exist_ok=True)
        md_dir = self.output_dir / "markdown"
        md_dir.mkdir(parents=True, exist_ok=True)

        cleaned_posts: List[CleanPost] = []
        movie_count = 0
        unlocked_count = 0

        for post in raw_posts:
            clean_item, media_title, year = self._build_clean_post_item(post)
            if year is not None:
                movie_count += 1
            if clean_item.is_unlocked:
                unlocked_count += 1
            cleaned_posts.append(clean_item)
            self._write_single_markdown_file(md_dir, clean_item, media_title, year)

        raw_json_path = self.output_dir / "posts_raw.json"
        with open(raw_json_path, "w", encoding="utf-8") as f:
            json.dump(raw_posts, f, indent=2, ensure_ascii=False)

        clean_json_path = self.output_dir / "posts_clean.json"
        with open(clean_json_path, "w", encoding="utf-8") as f:
            json.dump([asdict(cp) for cp in cleaned_posts], f, indent=2, ensure_ascii=False)

        summary = {
            "creator": self.creator,
            "fetched_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "total_posts": len(raw_posts),
            "unlocked_posts": unlocked_count,
            "movie_show_posts": movie_count,
            "output_directory": str(self.output_dir.resolve()),
            "files": {
                "raw_json": str(raw_json_path.name),
                "clean_json": str(clean_json_path.name),
                "markdown_dir": str(md_dir.name),
            },
        }
        summary_path = self.output_dir / "summary.json"
        with open(summary_path, "w", encoding="utf-8") as f:
            json.dump(summary, f, indent=2)

        print(f"\n[✓] Successfully downloaded and processed {len(raw_posts)} posts!\n")
        return summary


def load_dotenv(path: str | Path = ".env") -> None:
    """Load key-value pairs from a .env file into os.environ if not already present."""
    p = Path(path)
    if not p.is_file():
        return
    try:
        with open(p, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#") or "=" not in line:
                    continue
                k, v = line.split("=", 1)
                k = k.strip()
                v = v.strip().strip("'\"")
                if k and k not in os.environ:
                    os.environ[k] = v
    except Exception:
        pass


def _build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Download all Buy Me a Coffee posts by thetimestampdudes for JCF creation."
    )
    parser.add_argument("--config", default=None, help="Path to optional config file")
    parser.add_argument("--creator", default=DEFAULT_CREATOR, help="Creator slug")
    parser.add_argument("--session-file", default=DEFAULT_SESSION_FILE, help="Path to session file")
    parser.add_argument("--output-dir", default=DEFAULT_OUTPUT_DIR, help="Output directory")
    parser.add_argument("--login", action="store_true", help="Force browser login")
    parser.add_argument("--email", default=None, help="BMC account email")
    parser.add_argument("--password", default=None, help="BMC account password")
    parser.add_argument("--no-headless", action="store_true", help="Run browser visibly")
    return parser


def _load_args_config(args: argparse.Namespace) -> None:
    if not args.config:
        return
    cfg_path = Path(args.config)
    if not cfg_path.is_file():
        return
    if cfg_path.suffix.lower() == ".json":
        try:
            with open(cfg_path, "r", encoding="utf-8") as f:
                cfg = json.load(f)
            if not args.email and "email" in cfg:
                args.email = cfg["email"]
            if not args.password and "password" in cfg:
                args.password = cfg["password"]
            if "creator" in cfg and args.creator == DEFAULT_CREATOR:
                args.creator = cfg["creator"]
        except Exception as e:
            print(f"[!] Warning: Failed to parse config JSON: {e}", file=sys.stderr)
    else:
        load_dotenv(cfg_path)


def _handle_auth(client: BuyMeACoffeeClient, args: argparse.Namespace) -> None:
    has_session = client.load_session()
    is_authed = has_session and client.is_authenticated()
    if is_authed and not args.login:
        print(f"[✓] Active authenticated session found in '{args.session_file}'.")
        return

    email = args.email or os.environ.get("BMC_EMAIL") or input("Buy Me a Coffee Email: ").strip()
    import getpass

    password = (
        args.password
        or os.environ.get("BMC_PASSWORD")
        or getpass.getpass("Buy Me a Coffee Password: ")
    )
    ok = client.login_browser(email=email, password=password, headless=not args.no_headless)
    if not ok or not client.is_authenticated():
        print("[!] Authentication failed.", file=sys.stderr)
        sys.exit(1)


def main() -> None:
    load_dotenv(".env")
    args = _build_arg_parser().parse_args()
    _load_args_config(args)
    client = BuyMeACoffeeClient(
        creator=args.creator,
        session_file=args.session_file,
        output_dir=args.output_dir,
    )
    _handle_auth(client, args)
    raw_posts = client.fetch_all_posts()
    if not raw_posts:
        print("[!] No posts could be retrieved.", file=sys.stderr)
        sys.exit(1)
    client.process_and_export(raw_posts)


if __name__ == "__main__":
    main()
