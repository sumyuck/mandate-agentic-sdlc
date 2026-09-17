# Mandate - developer and reviewer entry points.
# Every target here is expected to work from a clean clone.

SHELL := /bin/bash
.DEFAULT_GOAL := help

# A machine with several .NET installations resolves `dotnet` to whichever comes
# first on PATH, which is often not the one holding the SDK global.json pins.
# The build finds a suitable one rather than asking the reviewer to fix PATH.
# Override explicitly if you want a particular install:
#   make build DOTNET=$$HOME/.dotnet/dotnet
DOTNET ?= $(shell bash scripts/find-dotnet.sh)
CLI := $(DOTNET) run --project src/Mandate.Cli --

.PHONY: help doctor restore build test format lint info workflow diagram run run-model runs audit policy metrics report llm prompts clean verify demo

help: ## Show available targets
	@grep -hE '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) \
		| awk 'BEGIN{FS=":.*?## "}{printf "  \033[1m%-12s\033[0m %s\n", $$1, $$2}'

doctor: ## Check the toolchain matches global.json
	@DOTNET="$(DOTNET)" bash scripts/doctor.sh

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

# Set FAIL=<agent-id> to inject a failure and watch retry, fallback and rollback.
FAIL ?=

# Exit code 3 means the run parked for a human decision, which is the lifecycle working as
# designed rather than a failed target. Only 0 and 3 are success here; 1 and 2 still fail.
RUN_OK = || { status=$$?; [ $$status -eq 3 ] || exit $$status; }

run: ## Execute the lifecycle with scripted agents (SCENARIO=..., FAIL=<agent>)
	@$(CLI) run "$(REQUEST)" --scenario $(SCENARIO) $(if $(FAIL),--fail $(FAIL),) $(RUN_OK)

run-model: ## Execute the lifecycle with model-backed agents (LLM=stub|replay|record|live)
	@$(CLI) run "$(REQUEST)" --scenario $(SCENARIO) --agents model --llm $(LLM) $(RUN_OK)

runs: ## List recorded runs
	@$(CLI) runs list

audit: ## Verify that no recorded run's audit log has been altered
	@$(CLI) audit verify

policy: ## List the security, compliance and change-control rules
	@$(CLI) policy list

# Replay by default: no key, no network, no spend. `make llm LLM=live` calls the provider.
LLM ?= replay

llm: ## Prove the model layer works (LLM=replay|stub|record|live)
	@$(CLI) llm check --llm $(LLM)

prompts: ## List the versioned prompts a run may issue, with their fingerprints
	@$(CLI) llm prompts

# Usage: make metrics RUN=run_20260916T142500Z_a1b2c3
metrics: ## Show a run's reliability figures, derived from its log
	@$(CLI) runs metrics "$(RUN)"

report: ## Write a run as a self-contained HTML page
	@$(CLI) runs report "$(RUN)"

diagram: ## Regenerate the lifecycle diagram from the workflow definition
	@$(CLI) workflow render -o docs/diagrams/sdlc.v1.mmd

demo: ## Guided five-minute tour for a reviewer: offline, no API key
	@DOTNET="$(DOTNET)" bash scripts/demo.sh

verify: doctor build test lint ## Full local gate: toolchain, build, test, style
	@echo "verify: OK"

clean: ## Remove build output and local run state (recorded runs are deleted)
	$(DOTNET) clean --nologo >/dev/null
	rm -rf .mandate
