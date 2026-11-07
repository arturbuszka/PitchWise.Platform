#!/bin/bash
# Usage: run.sh <results dir> <candidate> <scenario> [--seconds N]
# One MediaMTX and one rig per run, capped so that a measurement can't take the machine down.
set -u
results=$1; candidate=$2; scenario=$3; shift 3
here=$(cd "$(dirname "$0")" && pwd)
out=$results/$candidate/$scenario
mkdir -p "$out"
docker rm -f pw-switcher-mtx pw-switcher-run >/dev/null 2>&1
docker network create pw-switcher >/dev/null 2>&1
docker run -d --name pw-switcher-mtx --network pw-switcher -v "$here/mediamtx.yml:/mediamtx.yml:ro,z" bluenviron/mediamtx:1.21.3 >/dev/null
sleep 1
docker run --name pw-switcher-run --rm --network pw-switcher --cpus=7 --memory=6g --user "$(id -u):$(id -g)" \
  -e HOME=/tmp -e FF_MODE="${FF_MODE:-wallclock}" -e PYTHONDONTWRITEBYTECODE=1 -v "$here:/rig:ro,z" -v "$out:/out:z" \
  pw-switcher-rig nice -n 5 python3 /rig/run.py "$candidate" "$scenario" /out "$@" >"$out/run.log" 2>&1
code=$?
docker logs pw-switcher-mtx >"$out/mediamtx.log" 2>&1
docker rm -f pw-switcher-mtx >/dev/null
docker network rm pw-switcher >/dev/null
echo "$candidate/$scenario exit $code"
