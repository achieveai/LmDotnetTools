# Prints stages and exits when they differ from the snapshot taken at start (or after $1 seconds).
cd /b/published/Reviewd
snap() { MSYS_NO_PATHCONV=1 wsl -d Ubuntu-24.04 -- python3 /mnt/b/published/Reviewd/stages.py 2>&1 | awk '{print $1, $2}'; }
a=$(snap); end=$((SECONDS+${1:-7200}))
while [ $SECONDS -lt $end ]; do sleep 60; b=$(snap); [ "$a" != "$b" ] && break; done
date -u +%T; MSYS_NO_PATHCONV=1 wsl -d Ubuntu-24.04 -- python3 /mnt/b/published/Reviewd/stages.py; tail -n 6 state/logs/batch.err.log | cut -c1-400
