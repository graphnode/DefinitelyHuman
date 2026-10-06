#!/bin/sh
# Runs on the server as the forced command of the GitHub Actions deploy key
# (installed as /usr/local/sbin/definitelyhuman-deploy, see CLAUDE.md "Deployment"): takes a
# gzipped tar of a `dotnet publish` output on stdin, unpacks it over the build and restarts the bot.
set -eu
tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT
cat > "$tmp"

# Refuse a cut-off or wrong upload before touching the running build.
gzip -t "$tmp"
tar -tzf "$tmp" | grep -qx './DefinitelyHuman.dll'

tar -xzf "$tmp" --no-same-owner -C /opt/definitelyhuman
systemctl restart definitelyhuman
sleep 6
systemctl is-active definitelyhuman
