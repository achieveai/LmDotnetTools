#!/usr/bin/env bash
# Installs a published build as releases/<cli|host>/<name> and points $REVIEWD_HOME/<cli|host> at it.
#   deploy.sh cli  <publish dir> <name>     # name: the commit it was built from
#   deploy.sh host <publish dir> <name>
# Stop the matching process first, then start it again with start-batch.sh or start-host.sh.
# Rollback: ln -sfn releases/<kind>/<older name> "$REVIEWD_HOME/<kind>", then restart.
set -euo pipefail
source "$(dirname "$(readlink -f "$0")")/env.sh"
kind=$1 src=$2 name=$3
case $kind in
    cli | host) ;;
    *) echo "kind must be cli or host"; exit 2 ;;
esac
rel=$REVIEWD_HOME/releases/$kind/$name
if [ -e "$rel" ]; then
    echo "$rel already exists"
    exit 1
fi
mkdir -p "$rel"
cp -a "$src/." "$rel/"
if [ "$kind" = host ]; then
    # Data and the deployed config stay in data/host; the published defaults are dropped from the copy.
    for item in logs workflow-index chat-modes workspaces appsettings.json appsettings.Development.json; do
        rm -rf -- "${rel:?}/$item"
        ln -s "$REVIEWD_HOME/data/host/$item" "$rel/$item"
    done
fi
ln -sfn "releases/$kind/$name" "$REVIEWD_HOME/$kind"
echo "$kind -> releases/$kind/$name"
