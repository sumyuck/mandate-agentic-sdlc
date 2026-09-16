# Mandate - developer and reviewer entry points.
# Every target here is expected to work from a clean clone.

SHELL := /bin/bash
.DEFAULT_GOAL := help

# Override if your .NET 8 SDK is not first on PATH, e.g.
#   make build DOTNET=$$HOME/.dotnet/dotnet
DOTNET ?= dotnet
CLI := $(DOTNET) run --project src/Mandate.Cli --

.PHONY: help doctor restore build test format lint info workflow diagram run clean verify

help: ## Show available targets
	@grep -hE '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) \
		| awk 'BEGIN{FS=":.*?## "}{printf "  \033[1m%-12s\033[0m %s\n", $$1, $$2}'

doctor: ## Check the toolchain matches global.json
	@printf 'requested SDK : %s\n' "$$(python3 -c 'import json;print(json.load(open("global.json"))["sdk"]["version"])')"
	@printf 'resolved SDK  : %s\n' "$$($(DOTNET) --version 2>&1)"
	@if $(DOTNET) --version >/dev/null 2>&1 && [[ "$$($(DOTNET) --version)" == 10.0.* ]]; then \
		echo "doctor: OK"; \
	else \
		echo "doctor: FAIL - a .NET 10 SDK is required (see docs/adr/0002-target-framework.md)."; \
		echo "        install: curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0"; \
		echo "        then:    export PATH=\"\$$HOME/.dotnet:\$$PATH\""; \
		exit 1; \
	fi

restore: ## Restore NuGet packages
	$(DOTNET) restore

build: ## Build the solution (warnings are errors)
	$(DOTNET) build --nologo

test: ## Run all tests
	$(DOTNET) test --nologo

format: ## Apply code style fixes
	$(DOTNET) format

lint: ## Verify code style without modifying files
	$(DOTNET) format --verify-no-changes

info: ## Print engine identity and host diagnostics
	@$(CLI) info

workflow: ## Validate the lifecycle definition and show its parallel structure
	@$(CLI) workflow validate

# Override the scenario, e.g. `make run SCENARIO=brownfield`.
SCENARIO ?= greenfield
REQUEST ?= Build a URL shortener with create and redirect APIs

run: ## Execute the lifecycle against scripted agents (SCENARIO=greenfield|brownfield|ambiguous)
	@$(CLI) run "$(REQUEST)" --scenario $(SCENARIO)

diagram: ## Regenerate the lifecycle diagram from the workflow definition
	@$(CLI) workflow render -o docs/diagrams/sdlc.v1.mmd

verify: doctor build test lint ## Full local gate: toolchain, build, test, style
	@echo "verify: OK"

clean: ## Remove build output and local run state
	$(DOTNET) clean --nologo >/dev/null
	rm -rf .mandate
