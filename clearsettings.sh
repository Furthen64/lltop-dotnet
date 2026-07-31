#!/bin/sh

set -eu

config_dir="${HOME:?HOME is not set}/.config/lltop"

echo "This will permanently delete all lltop settings and data in:"
echo "  $config_dir/*"
printf "Continue? [y/N] "
IFS= read -r answer

case "$answer" in
    y|Y)
        if [ -d "$config_dir" ]; then
            find "$config_dir" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +
            echo "Cleared $config_dir"
        else
            echo "Nothing to clear; $config_dir does not exist."
        fi
        ;;
    *)
        echo "Cancelled. Nothing was deleted."
        ;;
esac
