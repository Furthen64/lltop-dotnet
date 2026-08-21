# Try the model catalog refresh

The catalog refresh is optional. It looks up public information about models;
it does not download models, start Ollama, or modify your lltop profiles.

## Quick test

From the lltop repository directory, run:

```sh
./refresh_models.sh
```

Choose a source when asked:

1. Hugging Face
2. Ollama Library
3. ModelScope
4. All three

For the most complete first try, choose **4**.

When it finishes, you should see one successful line for each selected source,
similar to:

```text
ollama-library: added 640, updated 0, removed 0 (640 total entries)
huggingface: added 100, updated 0, removed 0 (100 total entries)
modelscope: added 50, updated 0, removed 0 (50 total entries)
Catalog database: /home/you/.local/share/lltop/model-catalog.sqlite
```

The numbers are not fixed; online catalogs change. What matters is that each
source reports its added/updated/removed counts and there is no error message.
The normal refresh is bounded: Hugging Face imports at most 100 models,
ModelScope at most 50, and Ollama at most 100 families with 12 listed variants
per family. Use `--limit N` (from 1 to 100) to lower the Hugging Face/Ollama
limit for a quicker test.

After a successful refresh, that source is reused for seven days. To deliberately
refresh it again before then, add `--force`:

```sh
./refresh_models.sh --force
```

## Browse the catalog

After refreshing, view the Ollama Library entries with:

```sh
./catalog.sh ollama
```

Or search every source at once:

```sh
./catalog.sh --search qwen
```

The viewer is read-only. It shows a compact table and limits its output to 40
rows by default; add `--limit 100` when you want more results.

Sort a source by model family or parameter size:

```sh
./catalog.sh ollama --sort family
./catalog.sh ollama --sort size
```

Size sorting puts the largest recognized parameter sizes first.

## Browse Hugging Face quantizations for one model

Use a canonical Hugging Face base-model ID to fetch a small, linked GGUF
shortlist on demand:

```sh
./catalog.sh --quantizations Qwen/Qwen2.5-7B-Instruct
```

The command checks candidate GGUF repositories for an explicit `base_model`
link, caches the three most-downloaded matches, and shows one representative
GGUF file and its size for each. It does not download weights. Use `--refresh`
to fetch the shortlist again; combine it with `--top N` to cache more than the
default three repositories. A lookup is capped at 10 repositories.

## Try another source

Run the same command again and select just one source. This is a quick way to
check each catalog independently:

```sh
./refresh_models.sh
```

Refreshing a source updates its saved information; it does not create duplicate
entries or affect the other sources.

## If it fails

Check that you have internet access, then try again. A temporary source failure
does not remove information from a previous successful refresh.

## Where the catalog is stored

lltop creates the catalog automatically at:

```text
~/.local/share/lltop/model-catalog.sqlite
```

You do not need to open or manage this database. It is included here only so
you know where the cached catalog lives.

## Current scope

This first version only gathers and caches catalog information. lltop does not
yet use it to identify your downloaded GGUF files or alter launch settings.
Descriptions containing more than one Chinese character are stored as
`<Chinese description>` so a future English interface does not display
untranslated catalog text.
