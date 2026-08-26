#!/usr/bin/env python3
"""Interactive viewer for lltop run-*.dat files (requires matplotlib)."""
import argparse
import csv
import os
from datetime import datetime

import matplotlib.dates as mdates
import matplotlib.pyplot as plt
from matplotlib.animation import FuncAnimation


def number(value):
    return float(value) if value else None


def read_source(path):
    samples, events = [], []
    with open(path, newline="", encoding="utf-8") as source:
        rows = (line for line in source if not line.startswith("#"))
        for row in csv.DictReader(rows, delimiter="\t"):
            try:
                stamp = datetime.fromisoformat(row["timestamp_utc"].replace("Z", "+00:00"))
            except (KeyError, ValueError):
                continue  # ignore a partially-written final line while following
            if row["kind"] == "sample":
                samples.append((stamp, {key: number(row.get(key, "")) for key in (
                    "cpu_percent", "gpu_percent", "system_ram_used_bytes", "vram_used_bytes")}))
            elif row.get("label"):
                events.append((stamp, row["kind"], row["label"]))
    return samples, events


def main():
    parser = argparse.ArgumentParser(description="Pan/zoom viewer for lltop realtime graph data.")
    parser.add_argument("data", help="Path to run-*.dat")
    parser.add_argument("--metrics", default="vram,ram,cpu,gpu", help="Comma-separated: vram,ram,cpu,gpu")
    parser.add_argument("--events", default="all", help="Comma-separated event kinds, or 'all'/'none'")
    parser.add_argument("--follow", action="store_true", help="Reload the file every second while it is being written")
    args = parser.parse_args()
    metrics = {item.strip() for item in args.metrics.split(",")}
    wanted_events = None if args.events == "all" else set() if args.events == "none" else set(args.events.split(","))
    fig, (memory, utilization) = plt.subplots(2, 1, sharex=True, layout="constrained")
    fig.canvas.manager.set_window_title("lltop realtime graph")

    def draw(_=None):
        samples, events = read_source(args.data)
        left, right = plt.xlim() if memory.lines else (None, None)
        memory.clear(); utilization.clear()
        if samples:
            times = [sample[0] for sample in samples]
            if "vram" in metrics: memory.plot(times, [sample[1]["vram_used_bytes"] / 2**30 if sample[1]["vram_used_bytes"] is not None else None for sample in samples], label="VRAM (GiB)")
            if "ram" in metrics: memory.plot(times, [sample[1]["system_ram_used_bytes"] / 2**30 if sample[1]["system_ram_used_bytes"] is not None else None for sample in samples], label="System RAM (GiB)")
            if "cpu" in metrics: utilization.plot(times, [sample[1]["cpu_percent"] for sample in samples], label="CPU (%)")
            if "gpu" in metrics: utilization.plot(times, [sample[1]["gpu_percent"] for sample in samples], label="GPU (%)")
        for stamp, kind, label in events:
            if wanted_events is not None and kind not in wanted_events:
                continue
            for axis in (memory, utilization):
                axis.axvline(stamp, color="tab:red" if kind == "error" else "0.5", alpha=.35, linewidth=.8)
            memory.annotate(label, (stamp, 1), xycoords=("data", "axes fraction"), xytext=(3, -3), textcoords="offset points", rotation=90, va="top", fontsize=8)
        memory.set_ylabel("Memory (GiB)"); utilization.set_ylabel("Utilization (%)")
        utilization.set_xlabel("Time (toolbar: pan/zoom)")
        memory.legend(loc="upper left"); utilization.legend(loc="upper left")
        utilization.xaxis.set_major_formatter(mdates.DateFormatter("%H:%M:%S"))
        if left is not None and not args.follow: plt.xlim(left, right)

    draw()
    if args.follow:
        FuncAnimation(fig, draw, interval=1000, cache_frame_data=False)
    plt.show()


if __name__ == "__main__":
    main()
