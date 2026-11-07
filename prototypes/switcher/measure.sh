#!/bin/bash
# The whole series, one scenario at a time. Usage: [ONLY=ffmpeg|gst] measure.sh <results dir>
set -u
here=$(cd "$(dirname "$0")" && pwd)
R=$1
analyse() {
  docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -e PYTHONDONTWRITEBYTECODE=1 \
    -v "$here:/rig:ro,z" -v "$1:/out:z" pw-switcher-rig nice -n 10 python3 /rig/analyse.py /out >"$1/analysis.txt" 2>"$1/analysis.err"
}
one() {  # <variant> <candidate> <scenario> [args]
  local variant=$1 candidate=$2 scenario=$3; shift 3
  if [ -n "${ONLY:-}" ] && [ "$ONLY" != "$candidate" ]; then return; fi
  local available; available=$(free -m | awk 'NR==2{print $7}')
  if [ "$available" -lt 2500 ]; then echo "STOP: only ${available} MB free"; exit 1; fi
  echo "$(date +%T) $variant $scenario (free ${available} MB, load $(cut -d' ' -f1 /proc/loadavg))"
  FF_MODE=$variant "$here/run.sh" "$R/$variant" "$candidate" "$scenario" "$@"
  analyse "$R/$variant/$candidate/$scenario"
}
# Every scenario, for ffmpeg with stream timestamps (quick) and arrival timestamps (wallclock), and for GStreamer.
for scenario in loss_offair loss_onair hang_offair hang_onair loss_audio restart start_missing; do
  one quick ffmpeg "$scenario"
  one gst gst "$scenario"
done
one quick_copyaudio ffmpeg loss_offair
one quick ffmpeg clean
one gst gst clean
one wallclock ffmpeg clean --seconds 180
one quick ffmpeg drift
one wallclock ffmpeg drift
one gst gst drift
one quick ffmpeg cpu1080
one gst gst cpu1080
echo ALL-DONE
