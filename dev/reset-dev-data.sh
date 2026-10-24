#!/usr/bin/env bash
set -euo pipefail

# Resets the local dev environment for working on the site by hand:
#   1. drops and recreates the dev database (blog by default),
#   2. applies the EF migrations to it,
#   3. empties the dev uploads folder (./uploads by default).
#
# DESTRUCTIVE: it drops the whole database and deletes every uploaded file. It asks for
# confirmation first; --yes skips the prompt. Run it with the dev stack stopped (open
# database connections make the drop fail).
#
# It deliberately does NOT load any data. Afterwards dev/seed-dev-data.sh can optionally
# seed a small sample through the running API — a separate step, so resetting and seeding
# are never entangled.
#
# The defaults come from dev/instance-params.sh (dev instance: database blog, uploads
# ./uploads). --db NAME and --uploads DIR override them, e.g. to rehearse this script
# against a scratch database without touching the dev one.

DEV="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$DEV/.." && pwd)"
cd "$ROOT"

# global.json pins SDK 11.0.100 (RC 2). Fedora ships 10.0.1xx in /usr/bin, so prefer ~/dotnet when present.
if [ -x "$HOME/dotnet/dotnet" ]; then
    export DOTNET_ROOT="$HOME/dotnet"
    export PATH="$HOME/dotnet:$PATH"
fi

source "$DEV/instance-params.sh"

usage() {
    echo "Usage: dev/reset-dev-data.sh [--yes] [--db NAME] [--uploads DIR]"
    echo "  Drops and recreates the dev database, applies the migrations and empties the"
    echo "  uploads folder. Defaults (dev/instance-params.sh): database 'blog', uploads ./uploads."
    echo "  --yes          skip the confirmation prompt"
    echo "  --db NAME      reset NAME instead of the dev database"
    echo "  --uploads DIR  empty DIR instead of the dev uploads folder"
}

ASSUME_YES=0
DB_OVERRIDE=""
UPLOADS_OVERRIDE=""
while [ $# -gt 0 ]; do
    case "$1" in
        --yes) ASSUME_YES=1 ;;
        --db) shift; DB_OVERRIDE="${1:?--db needs a database name}" ;;
        --uploads) shift; UPLOADS_OVERRIDE="${1:?--uploads needs a directory}" ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

if [ ! -f .env ]; then
    echo "Creating .env from .env.example"
    cp .env.example .env
fi

# Load .env into the current shell.
# .env values may contain semicolons, so a plain `source` would truncate them.
while IFS='=' read -r key value; do
    case "$key" in
        ''|\#*) continue ;;
    esac
    export "$key=$value"
done < .env

instance_params dev
TARGET_DB="$INSTANCE_DB"
TARGET_CONN="$INSTANCE_CONN"
TARGET_UPLOADS="$INSTANCE_UPLOADS"

if [ -n "$DB_OVERRIDE" ]; then
    TARGET_DB="$DB_OVERRIDE"
    TARGET_CONN="$(printf '%s' "$INSTANCE_CONN" | sed -E "s/(database=)[^;]*/\\1$TARGET_DB/I")"
fi
if [ -n "$UPLOADS_OVERRIDE" ]; then
    TARGET_UPLOADS="$UPLOADS_OVERRIDE"
fi

# Fail closed: nothing destructive runs unless the target is exactly what we think it is.
if [ -z "$TARGET_DB" ] || [ -z "$TARGET_CONN" ]; then
    echo "Refusing: no database target (is ConnectionStrings__BlogDb set in .env?)" >&2
    exit 1
fi
if [ "$(conn_db_name "$TARGET_CONN")" != "$TARGET_DB" ]; then
    echo "Refusing: the connection string does not target '$TARGET_DB' exactly" >&2
    exit 1
fi
case "$TARGET_UPLOADS" in
    ''|/|.|..|"$ROOT"|"$DEV"|"$HOME")
        echo "Refusing: unsafe uploads folder '$TARGET_UPLOADS'" >&2
        exit 1 ;;
esac

echo "This will:"
echo "  - DROP the database '$TARGET_DB' and recreate it"
echo "  - apply the EF migrations to it"
echo "  - DELETE every file in '$TARGET_UPLOADS'"
if [ "$ASSUME_YES" != 1 ]; then
    printf "Continue? [y/N] "
    reply=""
    read -r reply || true
    case "$reply" in
        [yY]|[yY][eE][sS]) ;;
        *) echo "Aborted: nothing was changed."; exit 1 ;;
    esac
fi

echo "Building solution ..."
dotnet build Croft.slnx -nologo -v quiet

echo "Recreating database $TARGET_DB ..."
# database drop takes no --connection; it drops the context's configured database, so the
# target connection string is passed via the environment. The dry run names the target and
# must name it exactly before anything is dropped (same guarded drop as start-local.sh).
drop_target="$(ConnectionStrings__BlogDb="$TARGET_CONN" \
    dotnet ef database drop --dry-run -p src/Croft.Api -s src/Croft.Api --no-build 2>&1)"
echo "$drop_target"
if [[ "$drop_target" != *"the database '$TARGET_DB'"* ]]; then
    echo "Refusing to drop: the dry run did not name '$TARGET_DB' (got: $drop_target)" >&2
    exit 1
fi
ConnectionStrings__BlogDb="$TARGET_CONN" \
    dotnet ef database drop --force -p src/Croft.Api -s src/Croft.Api --no-build

echo "Applying database migrations ..."
ConnectionStrings__BlogDb="$TARGET_CONN" \
    dotnet ef database update -p src/Croft.Api -s src/Croft.Api --no-build --connection "$TARGET_CONN"

echo "Emptying uploads folder $TARGET_UPLOADS ..."
rm -rf "$TARGET_UPLOADS"
mkdir -p "$TARGET_UPLOADS"

echo ""
echo "Done. The database '$TARGET_DB' is migrated and empty, and '$TARGET_UPLOADS' is empty."
echo "Optional next step (a separate step, this script never seeds): with a stack running,"
echo "load the small sample data through the API:"
echo "  ./start-local.sh && dev/seed-dev-data.sh"
