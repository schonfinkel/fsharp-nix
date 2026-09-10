.DEFAULT_GOAL := build
.DELETE_ON_ERROR:

PROJECT_NAME ?= fsnix
DOTNET ?= dotnet
FANTOMAS ?= fantomas
NIX ?= nix

SOLUTION := fsnix.slnx
UNIT_TEST_PROJECT := tests/App.UnitTests/App.UnitTests.fsproj
DATABASE_TEST_PROJECT := tests/App.DatabaseTests/App.DatabaseTests.fsproj
HTTP_TEST_PROJECT := tests/App.HttpTests/App.HttpTests.fsproj
DATABASE_PROJECT := src/App.Database/App.Database.fsproj
SQLHYDRA_CONFIG := sqlhydra-npgsql.toml
GENERATED_SCHEMA := src/App.Database/Database.Generated.fs
NUGET_PACKAGES_DIR := out/nix-lock
NUGET_TO_JSON ?= nixpkgs\#nuget-to-json

PROJECT_FILES := $(wildcard src/*/*.fsproj tests/*/*.fsproj)
RESTORE_INPUTS := Makefile $(SOLUTION) global.json $(PROJECT_FILES) \
	$(wildcard Directory.Build.* Directory.Packages.* NuGet.Config)

.PHONY: build test test-unit test-integration test-database test-http migrate run totp db schema-generate schema-check nix-lock

build:
	$(DOTNET) build $(SOLUTION) -m:1

test: test-unit test-integration

test-unit: build
	$(DOTNET) run --project $(UNIT_TEST_PROJECT) --no-build

test-integration: test-database test-http

test-database: build
	$(DOTNET) run --project $(DATABASE_TEST_PROJECT) --no-build

test-http: build
	$(DOTNET) run --project $(HTTP_TEST_PROJECT) --no-build

migrate:
	$(DOTNET) run --project src/App/App.fsproj -- --migrate

run:
	$(DOTNET) run --project src/App/App.fsproj

totp:
	@$(DOTNET) fsi scripts/generate-totp.fsx

db:
	PGPASSWORD=${PROJECT_NAME} psql -U ${PROJECT_NAME} ${PROJECT_NAME}

schema-generate:
	$(DOTNET) build $(DATABASE_PROJECT) --no-restore
	$(DOTNET) sqlhydra npgsql -t $(SQLHYDRA_CONFIG) -p $(DATABASE_PROJECT)
	$(FANTOMAS) $(GENERATED_SCHEMA)
	$(DOTNET) build $(DATABASE_PROJECT) --no-restore

schema-check:
	$(DOTNET) build $(DATABASE_PROJECT) --no-restore
	DOTNET='$(DOTNET)' FANTOMAS='$(FANTOMAS)' bash scripts/verify-sqlhydra.sh

nix-lock: deps.json

deps.json: $(RESTORE_INPUTS)
	$(RM) -r $(NUGET_PACKAGES_DIR)
	$(DOTNET) restore --packages $(NUGET_PACKAGES_DIR) -m:1
	$(NIX) run '$(NUGET_TO_JSON)' -- $(NUGET_PACKAGES_DIR) > $@.tmp
	mv $@.tmp $@
