#!/usr/bin/env python3
"""Fetch a compact, offline-friendly model catalog from public model libraries."""

from __future__ import annotations

import argparse
import html
import json
import os
import re
import sqlite3
import sys
from datetime import UTC, datetime
from pathlib import Path
from urllib.error import URLError
from urllib.request import Request, urlopen


OLLAMA_LIBRARY_URL = "https://ollama.com/library?sort=popular"
HUGGING_FACE_URL = (
    "https://huggingface.co/api/models?pipeline_tag=text-generation&sort=downloads"
    "&direction=-1&limit={limit}&full=true"
)
MODELSCOPE_URL = (
    "https://modelscope.cn/openapi/v1/models?filter.task=text-generation"
    "&sort=downloads&page_size={limit}"
)
CAPABILITY_TAGS = {"vision", "tools", "thinking", "embedding", "audio", "cloud"}
INTENDED_USE_TAGS = {"code", "coding", "agent", "agents", "reasoning", "multilingual", "math", "rag"}


def default_database() -> Path:
    data_home = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local" / "share"))
    return data_home / "lltop" / "model-catalog.sqlite"


def fetch(url: str) -> bytes:
    request = Request(url, headers={"User-Agent": "lltop-dotnet-model-catalog/0.1"})
    with urlopen(request, timeout=30) as response:
        return response.read()


def clean_html(value: str) -> str:
    value = re.sub(r"<[^>]+>", " ", value)
    return " ".join(html.unescape(value).split())


def ollama_entries() -> list[dict[str, object]]:
    page = fetch(OLLAMA_LIBRARY_URL).decode("utf-8")
    cards = re.finditer(
        r'<li[^>]*>\s*<a href="/library/(?P<id>[^"]+)"[^>]*>(?P<body>.*?)</a>\s*</li>',
        page,
        re.DOTALL,
    )
    entries: list[dict[str, object]] = []
    for card in cards:
        family = card.group("id")
        body = card.group("body")
        description_match = re.search(r'<p class="max-w-lg[^"]*">(?P<text>.*?)</p>', body, re.DOTALL)
        description = clean_html(description_match.group("text")) if description_match else ""
        tags = {
            clean_html(match.group("text")).lower()
            for match in re.finditer(r'<span[^>]*class="[^"]*bg-indigo[^"]*"[^>]*>(?P<text>.*?)</span>', body, re.DOTALL)
        }
        capabilities = sorted(tags & CAPABILITY_TAGS)
        variants = [
            clean_html(match.group("text")).lower()
            for match in re.finditer(r'<span[^>]*class="[^"]*bg-\[#ddf4ff\][^"]*"[^>]*>(?P<text>.*?)</span>', body, re.DOTALL)
        ]
        url = f"https://ollama.com/library/{family}"
        # Library badges are family-level. Do not claim that every size supports them.
        entries.append({"model_id": family, "family": family, "variant": None, "description": description,
                        "capabilities": capabilities, "intended_uses": [], "capability_scope": "family", "url": url})
        for variant in dict.fromkeys(variants):
            entries.append({"model_id": f"{family}:{variant}", "family": family, "variant": variant,
                            "description": "", "capabilities": [], "intended_uses": [],
                            "capability_scope": "none", "url": url})
    if not entries:
        raise ValueError("Ollama Library page returned no recognizable model cards.")
    return entries


def huggingface_entries(limit: int) -> list[dict[str, object]]:
    payload = json.loads(fetch(HUGGING_FACE_URL.format(limit=limit)))
    if not isinstance(payload, list):
        raise ValueError("Hugging Face API did not return a model list.")
    entries: list[dict[str, object]] = []
    for model in payload:
        model_id = model.get("id")
        if not isinstance(model_id, str) or not model_id:
            continue
        card = model.get("cardData") or {}
        tags = {str(tag).lower() for tag in model.get("tags") or []}
        card_tags = card.get("tags") or [] if isinstance(card, dict) else []
        tags.update(str(tag).lower() for tag in card_tags)
        entries.append({
            "model_id": model_id,
            "family": model_id,
            "variant": None,
            "description": str(card.get("model_name", "")) if isinstance(card, dict) else "",
            "capabilities": sorted(tags & CAPABILITY_TAGS),
            "intended_uses": sorted(tags & INTENDED_USE_TAGS),
            "capability_scope": "model",
            "url": f"https://huggingface.co/{model_id}",
        })
    if not entries:
        raise ValueError("Hugging Face API returned no usable model entries.")
    return entries


def modelscope_entries(limit: int) -> list[dict[str, object]]:
    payload = json.loads(fetch(MODELSCOPE_URL.format(limit=min(limit, 50))))
    data = payload.get("data") if isinstance(payload, dict) else None
    models = data.get("models") if isinstance(data, dict) else None
    if not isinstance(models, list):
        raise ValueError("ModelScope API did not return a model list.")

    entries: list[dict[str, object]] = []
    for model in models:
        if not isinstance(model, dict):
            continue
        model_id = model.get("id")
        if not isinstance(model_id, str) or not model_id:
            continue
        tags = {str(tag).lower() for tag in model.get("tags") or []}
        tags.update({tag.partition(":")[2] for tag in tags if ":" in tag})
        entries.append({
            "model_id": model_id,
            "family": model_id,
            "variant": None,
            "description": str(model.get("description") or model.get("display_name") or ""),
            "capabilities": sorted(tags & CAPABILITY_TAGS),
            "intended_uses": sorted(tags & INTENDED_USE_TAGS),
            "capability_scope": "model",
            "url": f"https://modelscope.cn/models/{model_id}",
        })
    if not entries:
        raise ValueError("ModelScope API returned no usable model entries.")
    return entries


def initialize_database(connection: sqlite3.Connection) -> None:
    connection.executescript(
        """
        CREATE TABLE IF NOT EXISTS catalog_sources (
            source TEXT PRIMARY KEY,
            url TEXT NOT NULL,
            fetched_at TEXT NOT NULL,
            status TEXT NOT NULL,
            detail TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE IF NOT EXISTS catalog_models (
            source TEXT NOT NULL,
            model_id TEXT NOT NULL,
            family TEXT NOT NULL,
            variant TEXT,
            description TEXT NOT NULL DEFAULT '',
            capabilities_json TEXT NOT NULL DEFAULT '[]',
            intended_uses_json TEXT NOT NULL DEFAULT '[]',
            capability_scope TEXT NOT NULL,
            url TEXT NOT NULL,
            fetched_at TEXT NOT NULL,
            PRIMARY KEY (source, model_id)
        );
        """
    )


def replace_source(connection: sqlite3.Connection, source: str, source_url: str, entries: list[dict[str, object]]) -> int:
    fetched_at = datetime.now(UTC).isoformat()
    rows = [
        (source, entry["model_id"], entry["family"], entry["variant"], entry["description"],
         json.dumps(entry["capabilities"]), json.dumps(entry["intended_uses"]), entry["capability_scope"],
         entry["url"], fetched_at)
        for entry in entries
    ]
    with connection:
        connection.execute("DELETE FROM catalog_models WHERE source = ?", (source,))
        connection.executemany(
            """INSERT INTO catalog_models
               (source, model_id, family, variant, description, capabilities_json, intended_uses_json,
                capability_scope, url, fetched_at)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            rows,
        )
        connection.execute(
            """INSERT INTO catalog_sources (source, url, fetched_at, status, detail)
               VALUES (?, ?, ?, 'ok', '')
               ON CONFLICT(source) DO UPDATE SET url = excluded.url, fetched_at = excluded.fetched_at,
                   status = excluded.status, detail = excluded.detail""",
            (source, source_url, fetched_at),
        )
    return len(rows)


def record_failure(connection: sqlite3.Connection, source: str, source_url: str, error: Exception) -> None:
    with connection:
        connection.execute(
            """INSERT INTO catalog_sources (source, url, fetched_at, status, detail)
               VALUES (?, ?, ?, 'failed', ?)
               ON CONFLICT(source) DO UPDATE SET url = excluded.url, fetched_at = excluded.fetched_at,
                   status = excluded.status, detail = excluded.detail""",
            (source, source_url, datetime.now(UTC).isoformat(), str(error)),
        )


def main() -> int:
    parser = argparse.ArgumentParser(description="Refresh lltop's local model catalog.")
    parser.add_argument("--source", choices=("huggingface", "ollama", "modelscope", "all"), required=True)
    parser.add_argument("--db", type=Path, default=default_database(), help="SQLite database path")
    parser.add_argument("--limit", type=int, default=100, help="Maximum popular Hugging Face models to import")
    args = parser.parse_args()
    if args.limit < 1:
        parser.error("--limit must be at least 1")

    args.db.parent.mkdir(parents=True, exist_ok=True)
    connection = sqlite3.connect(args.db)
    initialize_database(connection)
    jobs = []
    if args.source in ("ollama", "all"):
        jobs.append(("ollama-library", OLLAMA_LIBRARY_URL, ollama_entries))
    if args.source in ("huggingface", "all"):
        jobs.append(("huggingface", HUGGING_FACE_URL.format(limit=args.limit), lambda: huggingface_entries(args.limit)))
    if args.source in ("modelscope", "all"):
        jobs.append(("modelscope", MODELSCOPE_URL.format(limit=min(args.limit, 50)), lambda: modelscope_entries(args.limit)))

    failures = 0
    for source, source_url, load in jobs:
        try:
            count = replace_source(connection, source, source_url, load())
            print(f"{source}: stored {count} catalog entries")
        except (URLError, TimeoutError, ValueError) as error:
            record_failure(connection, source, source_url, error)
            print(f"{source}: refresh failed ({error})", file=sys.stderr)
            failures += 1
    print(f"Catalog database: {args.db}")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
