#!/usr/bin/env python3
"""Render a compact, read-only view of lltop's cached model catalog."""

from __future__ import annotations

import argparse
import json
import os
import re
import sqlite3
import sys
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import quote, urlencode
from urllib.request import Request, urlopen


SOURCE_ALIASES = {
    "huggingface": "huggingface",
    "hf": "huggingface",
    "ollama": "ollama-library",
    "ollama-library": "ollama-library",
    "modelscope": "modelscope",
}
SIZE_PATTERN = re.compile(r"(?<![a-z0-9])(\d+(?:\.\d+)?)\s*([mMbBtT])(?![a-z0-9])")
HUGGING_FACE_API = "https://huggingface.co/api/models"
MAX_QUANTIZATION_REPOS = 10


def default_database() -> Path:
    data_home = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local" / "share"))
    return data_home / "lltop" / "model-catalog.sqlite"


def text_list(value: str) -> str:
    try:
        items = json.loads(value)
    except json.JSONDecodeError:
        return "?"
    return ", ".join(items) if items else "-"


def short(value: str, width: int) -> str:
    value = " ".join(value.split()) or "-"
    return value if len(value) <= width else value[: width - 1] + "…"


def size_in_billions(model_id: str, variant: str | None) -> float | None:
    text = variant or model_id
    match = SIZE_PATTERN.search(text)
    if not match:
        return None
    value = float(match.group(1))
    unit = match.group(2).lower()
    return value / 1_000 if unit == "m" else value * 1_000 if unit == "t" else value


def fetch_json(url: str) -> object:
    request = Request(url, headers={"User-Agent": "lltop-dotnet-model-catalog/0.1"})
    with urlopen(request, timeout=30) as response:
        return json.loads(response.read())


def initialize_quantization_schema(db: sqlite3.Connection) -> None:
    db.executescript(
        """
        CREATE TABLE IF NOT EXISTS hf_quantization_refreshes (
            base_model TEXT PRIMARY KEY,
            fetched_at TEXT NOT NULL,
            status TEXT NOT NULL,
            detail TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE IF NOT EXISTS hf_quantization_repos (
            base_model TEXT NOT NULL,
            repo_id TEXT NOT NULL,
            downloads INTEGER NOT NULL,
            quantized_by TEXT NOT NULL DEFAULT '',
            fetched_at TEXT NOT NULL,
            PRIMARY KEY (base_model, repo_id)
        );
        CREATE TABLE IF NOT EXISTS hf_quantization_files (
            base_model TEXT NOT NULL,
            repo_id TEXT NOT NULL,
            filename TEXT NOT NULL,
            size_bytes INTEGER,
            PRIMARY KEY (base_model, repo_id, filename)
        );
        """
    )


def base_models(card_data: object) -> set[str]:
    if not isinstance(card_data, dict):
        return set()
    value = card_data.get("base_model")
    values = value if isinstance(value, list) else [value]
    return {item.casefold() for item in values if isinstance(item, str)}


def refresh_quantizations(db: sqlite3.Connection, base_model: str, top: int) -> int:
    query = base_model.rsplit("/", 1)[-1]
    search_url = f"{HUGGING_FACE_API}?" + urlencode({
        "search": query, "filter": "gguf", "sort": "downloads", "direction": "-1", "limit": 30,
    })
    candidates = fetch_json(search_url)
    if not isinstance(candidates, list):
        raise ValueError("Hugging Face search did not return a model list.")

    repositories: list[tuple[str, int, str, list[tuple[str, int | None]]]] = []
    expected_base = base_model.casefold()
    for candidate in candidates:
        if not isinstance(candidate, dict) or not isinstance(candidate.get("id"), str):
            continue
        repo_id = candidate["id"]
        details = fetch_json(f"{HUGGING_FACE_API}/{quote(repo_id, safe='/')}?blobs=true")
        if not isinstance(details, dict) or expected_base not in base_models(details.get("cardData")):
            continue
        files = [
            (str(file.get("rfilename")), file.get("size") if isinstance(file.get("size"), int) else None)
            for file in details.get("siblings", [])
            if isinstance(file, dict) and isinstance(file.get("rfilename"), str)
            and file["rfilename"].lower().endswith(".gguf")
        ]
        if not files:
            continue
        card_data = details.get("cardData") or {}
        quantized_by = card_data.get("quantized_by", "") if isinstance(card_data, dict) else ""
        repositories.append((repo_id, int(details.get("downloads") or 0), str(quantized_by), files))
        if len(repositories) == top:
            break

    fetched_at = datetime.now(UTC).isoformat()
    with db:
        db.execute("DELETE FROM hf_quantization_files WHERE base_model = ?", (base_model,))
        db.execute("DELETE FROM hf_quantization_repos WHERE base_model = ?", (base_model,))
        for repo_id, downloads, quantized_by, files in repositories:
            db.execute(
                "INSERT INTO hf_quantization_repos VALUES (?, ?, ?, ?, ?)",
                (base_model, repo_id, downloads, quantized_by, fetched_at),
            )
            db.executemany(
                "INSERT INTO hf_quantization_files VALUES (?, ?, ?, ?)",
                [(base_model, repo_id, filename, size) for filename, size in files],
            )
        db.execute(
            """INSERT INTO hf_quantization_refreshes VALUES (?, ?, 'ok', '')
               ON CONFLICT(base_model) DO UPDATE SET fetched_at = excluded.fetched_at,
                   status = excluded.status, detail = excluded.detail""",
            (base_model, fetched_at),
        )
    return len(repositories)


def format_gib(size_bytes: int | None) -> str:
    return "size unknown" if size_bytes is None else f"{size_bytes / 1024 ** 3:.1f} GiB"


def show_quantizations(db: sqlite3.Connection, base_model: str) -> int:
    rows = db.execute(
        """SELECT repo_id, downloads, quantized_by FROM hf_quantization_repos
           WHERE base_model = ? ORDER BY downloads DESC, repo_id""",
        (base_model,),
    ).fetchall()
    if not rows:
        print(f"No cached GGUF quantizations for {base_model}.")
        return 0
    print(f"Hugging Face GGUF quantizations for {base_model}")
    print(f"{'Repo':52} {'Downloads':>10} {'Quantized by':16} Example GGUF")
    print("-" * 112)
    for repo_id, downloads, quantized_by in rows:
        files = db.execute(
            """SELECT filename, size_bytes FROM hf_quantization_files
               WHERE base_model = ? AND repo_id = ? ORDER BY filename""",
            (base_model, repo_id),
        ).fetchall()
        preferred = next((file for file in files if "q4_k_m" in file[0].casefold()), files[0])
        print(f"{short(repo_id, 52):52} {downloads:10,} {short(quantized_by or '-', 16):16} {short(preferred[0], 38)} ({format_gib(preferred[1])})")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Show lltop's cached online model catalog.",
        epilog="Examples: ./catalog.sh ollama   ./catalog.sh --search qwen",
    )
    parser.add_argument("source", nargs="?", help="huggingface, ollama, or modelscope")
    parser.add_argument("--search", metavar="TEXT", help="case-insensitive search in model names and descriptions")
    parser.add_argument("--limit", type=int, default=40, help="maximum rows to show (default: 40)")
    parser.add_argument("--sort", choices=("source", "family", "model", "size"), default="source",
                        help="sort by source, family, model, or size (default: source)")
    parser.add_argument("--quantizations", metavar="BASE_MODEL", help="browse cached Hugging Face GGUF quantizations for a base model")
    parser.add_argument("--refresh", action="store_true", help="refresh an on-demand quantization lookup")
    parser.add_argument("--top", type=int, default=3, help="number of Hugging Face quantization repos to cache (default: 3)")
    parser.add_argument("--db", type=Path, default=default_database(), help="SQLite database path")
    args = parser.parse_args()
    if args.limit < 1:
        parser.error("--limit must be at least 1")
    if args.top < 1 or args.top > MAX_QUANTIZATION_REPOS:
        parser.error(f"--top must be between 1 and {MAX_QUANTIZATION_REPOS}")
    if args.source and args.source.lower() not in SOURCE_ALIASES:
        parser.error("source must be huggingface, ollama, or modelscope")
    if args.quantizations and (args.source or args.search):
        parser.error("--quantizations cannot be combined with a source or --search")
    if args.refresh and not args.quantizations:
        parser.error("--refresh is only used with --quantizations")

    if args.quantizations:
        args.db.parent.mkdir(parents=True, exist_ok=True)
        db = sqlite3.connect(args.db)
        initialize_quantization_schema(db)
        base_model = args.quantizations.strip()
        cached = db.execute("SELECT status FROM hf_quantization_refreshes WHERE base_model = ?", (base_model,)).fetchone()
        if args.refresh or cached is None:
            try:
                count = refresh_quantizations(db, base_model, args.top)
                print(f"Hugging Face: refreshed {count} linked GGUF quantization repos.")
            except (OSError, ValueError, json.JSONDecodeError) as error:
                with db:
                    db.execute(
                        """INSERT INTO hf_quantization_refreshes VALUES (?, ?, 'failed', ?)
                           ON CONFLICT(base_model) DO UPDATE SET fetched_at = excluded.fetched_at,
                               status = excluded.status, detail = excluded.detail""",
                        (base_model, datetime.now(UTC).isoformat(), str(error)),
                    )
                print(f"Hugging Face quantization refresh failed: {error}", file=sys.stderr)
                return 1
        return show_quantizations(db, base_model)

    if not args.db.is_file():
        print(f"No catalog database found at {args.db}. Run ./refresh_models.sh first.", file=sys.stderr)
        return 1

    source = SOURCE_ALIASES.get(args.source.lower()) if args.source else None
    clauses: list[str] = []
    parameters: list[object] = []
    if source:
        clauses.append("source = ?")
        parameters.append(source)
    if args.search:
        clauses.append("(model_id LIKE ? COLLATE NOCASE OR family LIKE ? COLLATE NOCASE OR description LIKE ? COLLATE NOCASE)")
        term = f"%{args.search}%"
        parameters.extend((term, term, term))
    where = f"WHERE {' AND '.join(clauses)}" if clauses else ""

    db = sqlite3.connect(f"file:{args.db}?mode=ro", uri=True)
    total = db.execute(f"SELECT count(*) FROM catalog_models {where}", parameters).fetchone()[0]
    rows = db.execute(
        f"""SELECT source, model_id, family, variant, capabilities_json, intended_uses_json, description
            FROM catalog_models {where}
            ORDER BY source, family, variant IS NOT NULL, variant, model_id""",
        parameters,
    ).fetchall()
    if not rows:
        print("No matching catalog entries.")
        return 0

    if args.sort == "family":
        rows.sort(key=lambda row: (row[2].casefold(), row[3] is not None, row[3] or "", row[0].casefold()))
    elif args.sort == "model":
        rows.sort(key=lambda row: row[1].casefold())
    elif args.sort == "size":
        rows.sort(key=lambda row: (
            size_in_billions(row[1], row[3]) is None,
            -(size_in_billions(row[1], row[3]) or 0),
            row[2].casefold(),
        ))
    rows = rows[:args.limit]

    print(f"Catalog: {total} matching entries" + (f" (showing {len(rows)})" if total > len(rows) else "") + f" · sorted by {args.sort}")
    print(f"{'Source':18} {'Model':42} {'Capabilities':20} {'Uses':16} Description")
    print("-" * 132)
    for entry_source, model_id, family, variant, capabilities, uses, description in rows:
        model = model_id if variant is None else f"{family} [{variant}]"
        print(
            f"{short(entry_source, 18):18} {short(model, 42):42} {short(text_list(capabilities), 20):20} "
            f"{short(text_list(uses), 16):16} {short(description, 60)}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
