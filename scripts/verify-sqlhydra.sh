#!/usr/bin/env bash

set -euo pipefail

schema_file="src/App.Database/Database.Generated.fs"
project_file="src/App.Database/App.Database.fsproj"
schema_backup="$(mktemp)"
project_backup="$(mktemp)"

restore_files() {
    cp "$schema_backup" "$schema_file"
    cp "$project_backup" "$project_file"
    rm -f "$schema_backup" "$project_backup"
}

trap restore_files EXIT

cp "$schema_file" "$schema_backup"
cp "$project_file" "$project_backup"

"${DOTNET:-dotnet}" sqlhydra npgsql \
    -t sqlhydra-npgsql.toml \
    -p "$project_file"
"${FANTOMAS:-fantomas}" "$schema_file"

schema_changed=0
project_changed=0

if ! cmp -s "$schema_backup" "$schema_file"; then
    schema_changed=1
    diff -u "$schema_backup" "$schema_file" || diff_status=$?

    if [[ ${diff_status:-0} -gt 1 ]]; then
        exit "$diff_status"
    fi
fi

if ! cmp -s "$project_backup" "$project_file"; then
    project_changed=1
    diff -u "$project_backup" "$project_file" || diff_status=$?

    if [[ ${diff_status:-0} -gt 1 ]]; then
        exit "$diff_status"
    fi
fi

if [[ $schema_changed -ne 0 || $project_changed -ne 0 ]]; then
    echo "SqlHydra output is stale. Run 'make schema-generate' against the migrated database." >&2
    exit 1
fi

echo "SqlHydra output matches the live PostgreSQL schema."
