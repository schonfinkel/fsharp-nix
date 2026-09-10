.DEFAULT_GOAL := build
.DELETE_ON_ERROR:

PROJECT_NAME ?= fsnix
DOTNET ?= dotnet
NIX ?= nix

SOLUTION := fsnix.slnx
NUGET_PACKAGES_DIR := out/nix-lock
NUGET_TO_JSON ?= nixpkgs\#nuget-to-json

PROJECT_FILES := $(wildcard src/*/*.fsproj tests/*/*.fsproj)
RESTORE_INPUTS := Makefile $(SOLUTION) global.json $(PROJECT_FILES) \
	$(wildcard Directory.Build.* Directory.Packages.* NuGet.Config)

.PHONY: build test test-unit test-integration migrate run nix-lock

build:
	$(DOTNET) build $(SOLUTION)

test:
	$(DOTNET) test $(SOLUTION)

test-unit:
	$(DOTNET) test $(SOLUTION) --filter 'Category!=Integration'

test-integration:
	$(DOTNET) test $(SOLUTION) --filter 'Category=Integration'

migrate:
	$(DOTNET) run --project src/App/App.fsproj -- --migrate

run:
	$(DOTNET) run --project src/App/App.fsproj

db:
	PGPASSWORD=${PROJECT_NAME} psql -U ${PROJECT_NAME} ${PROJECT_NAME}

nix-lock: deps.json

deps.json: $(RESTORE_INPUTS)
	$(RM) -r $(NUGET_PACKAGES_DIR)
	$(DOTNET) restore --packages $(NUGET_PACKAGES_DIR)
	$(NIX) run '$(NUGET_TO_JSON)' -- $(NUGET_PACKAGES_DIR) > $@.tmp
	mv $@.tmp $@
