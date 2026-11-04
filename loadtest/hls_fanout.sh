#!/usr/bin/env bash
# Simulates N concurrent viewers fetching one HLS highlight from the nginx edge, to
# prove the scale story: the edge serves (and caches) every segment while the .NET API
# stays completely off the byte path.
#
# Usage:
#   loadtest/hls_fanout.sh <ANALYSIS_ID> <HIGHLIGHT_ID> [VIEWERS] [API] [EDGE]
#
# Example (defaults: 200 viewers, API :8000, edge :8080):
#   loadtest/hls_fanout.sh 3 5 200
#
# What it does:
#   1. Asks the API ONCE for a signed HLS manifest URL (the only API call).
#   2. Spawns VIEWERS parallel workers; each fetches the manifest + every segment.
#   3. Tallies HTTP codes and the X-Cache-Status header (MISS warms, HIT = edge cache).
#
# Then check `docker compose logs api` — it must show ZERO /hls/*.ts requests.
set -euo pipefail

ANALYSIS_ID="${1:?usage: hls_fanout.sh <analysis_id> <highlight_id> [viewers] [api] [edge]}"
HIGHLIGHT_ID="${2:?missing highlight_id}"
VIEWERS="${3:-200}"
API="${4:-http://localhost:8000}"
EDGE="${5:-http://localhost:8080}"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "→ requesting signed HLS URL from API (the one and only API call)…"
manifest_url="$(curl -fsS "${API}/api/analyses/${ANALYSIS_ID}/highlights/${HIGHLIGHT_ID}/hls" \
  | sed -n 's/.*"url"[ :]*"\([^"]*\)".*/\1/p')"
if [ -z "$manifest_url" ]; then
  echo "✗ API did not return an HLS url — is hls_ready true for this highlight?" >&2
  exit 1
fi
echo "  manifest: $manifest_url"

# Split the signed query (?md5=...&expires=...) so we can append it to each segment URL.
base="${manifest_url%/*}"            # http://localhost:8080/hls/{id}
query="${manifest_url#*\?}"          # md5=...&expires=...

echo "→ fetching manifest once to enumerate segments…"
segments="$(curl -fsS "$manifest_url" | grep -E '\.ts$' || true)"
seg_count="$(printf '%s\n' "$segments" | grep -c . || true)"
echo "  segments: $seg_count"
if [ "$seg_count" -eq 0 ]; then
  echo "✗ manifest had no .ts segments" >&2
  exit 1
fi

# One viewer = fetch manifest + every segment, recording "<http_code> <cache_status>".
viewer() {
  curl -s -o /dev/null -w '%{http_code} %header{x-cache-status}\n' "$manifest_url"
  while IFS= read -r seg; do
    [ -n "$seg" ] || continue
    curl -s -o /dev/null -w '%{http_code} %header{x-cache-status}\n' "${base}/${seg}?${query}"
  done <<< "$segments"
}
export -f viewer
export manifest_url base query segments

echo "→ unleashing ${VIEWERS} concurrent viewers…"
start=$(date +%s)
seq "$VIEWERS" | xargs -P "$VIEWERS" -I{} bash -c 'viewer' >> "$work/out.txt"
end=$(date +%s)

total=$(grep -c . "$work/out.txt" || true)
echo
echo "==== results (${VIEWERS} viewers × $((seg_count + 1)) requests = ${total} total, $((end - start))s) ===="
echo "HTTP codes:"
awk '{print $1}' "$work/out.txt" | sort | uniq -c
echo "X-Cache-Status:"
awk '{print ($2==""?"(none)":$2)}' "$work/out.txt" | sort | uniq -c
echo
echo "Expected: first viewer's segments MISS (warm the edge), the rest HIT."
echo "Now confirm the API never saw bytes:  docker compose logs api | grep -c '/hls/'   (should be 0)"
