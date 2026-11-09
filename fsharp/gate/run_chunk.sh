#!/bin/bash
# run_chunk.sh NAME IDS [WAIT] - one gate run of the F# engine on the models listed in IDS, as the brief
# requires: through runswmm_saves.sh (SAVE files backed up and restored), inside flock on the shared corpus lock
# (waiting at most WAIT seconds, default 4800, for another agent's run to finish), -j 1, a 1800 s timeout per
# model, --prune, the output folder in /dev/shm (lesson 22) and the wrapper's backups there too.
# Writes gate/NAME_run.txt and fsharp/gate/NAME_start.txt (start and end times). Written by Claude (Anthropic).
set -u
H=$(cd "$(dirname "$0")" && pwd); ROOT=$(cd "$H/../.." && pwd); G=$ROOT/gate
LOCK=${SWMM_GATE_LOCK:-${TMPDIR:-/tmp}/swmm5-gate.lock}  # one corpus run at a time
NAME=$1; IDS=$(readlink -f "$2"); WAIT=${3:-4800}
case "$NAME" in js_*|fs_*) ;; *) echo "run names start with js_ or fs_"; exit 2;; esac
ENGINE=${ENGINE:-$ROOT/fsharp/runswmm_saves.sh}
mkdir -p "/dev/shm/$NAME"
[ -L "$G/results/$NAME" ] && rm "$G/results/$NAME"
[ -d "$G/results/$NAME" ] && rmdir "$G/results/$NAME"
ln -s "/dev/shm/$NAME" "$G/results/$NAME"
echo "start $(date)  engine $ENGINE  ids $IDS" > "$H/${NAME}_start.txt"
cd "$G" && TMPDIR=/dev/shm flock -w "$WAIT" "$LOCK" nice -n 10 python3 gate.py "$NAME" "$ENGINE" --ids "$IDS" -j 1 --timeout 1800 --prune > "${NAME}_run.txt" 2>&1
rc=$?
echo "end   $(date)  rc $rc" >> "$H/${NAME}_start.txt"
if [ -z "$(ls -A "/dev/shm/$NAME")" ]; then             # every model passed and was pruned: an empty folder on disk
  rm "$G/results/$NAME"; mkdir "$G/results/$NAME"; rmdir "/dev/shm/$NAME"
fi
cat "$G/${NAME}_run.txt"
exit $rc
