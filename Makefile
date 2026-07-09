# CoRhino — command hub. Run `make` (or `make help`) to list targets.
#
# Thin wrappers around the canonical commands (dotnet / npm / yak / build-yak.sh)
# so day-to-day flows are one word. The build itself stays owned by the csproj
# (it runs the vite UI build + native flatten + install-to-plugin-dir), so
# `make build` is exactly `dotnet build` — no logic is duplicated here.

CSPROJ      := src/CoRhino.csproj
UI_DIR      := ui
TEST_SOURCE := https://test.yak.rhino3d.com

# OS-specific paths (override any with `make <target> YAK=... PLUGIN_DIR=...`).
UNAME := $(shell uname -s)
ifeq ($(UNAME),Darwin)
  YAK        ?= /Applications/Rhino 8.app/Contents/Resources/bin/yak
  PLUGIN_DIR ?= $(HOME)/Library/Application Support/McNeel/Rhinoceros/8.0/MacPlugIns/CoRhino
else
  YAK        ?= /c/Program Files/Rhino 8/System/Yak.exe
  PLUGIN_DIR ?= $(APPDATA)/McNeel/Rhinoceros/myplugins/CoRhino
endif

.DEFAULT_GOAL := help

## help: list available targets
.PHONY: help
help:
	@echo "CoRhino make targets:"
	@grep -E '^## ' $(MAKEFILE_LIST) | sed 's/^## /  /'

# ── Build ────────────────────────────────────────────────────────────────────

## build: Debug build (builds UI, flattens native libs, installs to the Rhino plugin dir)
.PHONY: build
build:
	dotnet build $(CSPROJ)

## release: Release build (same pipeline as build, Release config)
.PHONY: release
release:
	dotnet build $(CSPROJ) -c Release

# ── UI (ui/, vite + xterm.js) ─────────────────────────────────────────────────

## ui-install: install UI npm dependencies
.PHONY: ui-install
ui-install:
	cd $(UI_DIR) && npm install

## ui-dev: run the vite dev server (then launch Rhino with CORHINO_WEBUI_URL=http://localhost:5173)
.PHONY: ui-dev
ui-dev:
	@echo "Set CORHINO_WEBUI_URL=http://localhost:5173 before starting Rhino to load this dev server."
	cd $(UI_DIR) && npm run dev

## ui-build: build the UI bundle only (ui/dist)
.PHONY: ui-build
ui-build:
	cd $(UI_DIR) && npm run build

# ── Packaging / distribution (yak) ────────────────────────────────────────────

## pack: Release build + assemble the yak package into dist/yak/ (override version: make pack VERSION=0.1.0)
.PHONY: pack
pack:
	./build-yak.sh $(VERSION)

## yak-login: authorize the yak CLI with your Rhino account (browser, ~30 day token)
.PHONY: yak-login
yak-login:
	"$(YAK)" login

## yak-push-test: push all built packages to the TEST server (wiped nightly)
.PHONY: yak-push-test
yak-push-test:
	@for f in dist/yak/*.yak; do echo ">> push (test) $$f"; "$(YAK)" push --source $(TEST_SOURCE) "$$f"; done

## yak-push: push all built packages to the PRODUCTION server
.PHONY: yak-push
yak-push:
	@for f in dist/yak/*.yak; do echo ">> push $$f"; "$(YAK)" push "$$f"; done

# ── Housekeeping ──────────────────────────────────────────────────────────────

## clean: remove build outputs (bin/ obj/ dist/ ui/dist)
.PHONY: clean
clean:
	rm -rf src/bin src/obj dist $(UI_DIR)/dist

## uninstall: remove the locally installed plugin from the Rhino plugin dir
.PHONY: uninstall
uninstall:
	rm -rf "$(PLUGIN_DIR)"
	@echo "Removed $(PLUGIN_DIR)"
