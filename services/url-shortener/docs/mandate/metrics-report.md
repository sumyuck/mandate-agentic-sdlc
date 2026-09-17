# Metrics Report — URL Shortener Service Run

## Scope of this report

This reports what is available in the run context about the run itself (stages, retries,
human interventions). Where a figure was not supplied to this stage, it is stated as not
available rather than estimated.

## Run identity

- Workflow: `sdlc@v1`
- Scenario: `greenfield`
- Initiated by: `human:muskan`
- Existing code at start: `false`

## Stage outputs recorded

The following artifacts were produced over the run, per the upstream artifact list:

- `docs/mandate/request.md`
- `docs/requirements.md`
- `docs/ambiguity.md`
- `docs/design.md`
- `docs/adr/0001-sqlite-write-serialization.md`
- `contracts/openapi.yaml`
- 8 source patches (`Program.cs`, `Models.cs`, `UrlValidator.cs`, `CodeGenerator.cs`,
  `SchemaInitializer.cs`, `LinkRepository.cs`, `LinkService.cs`, `appsettings.json`)
- `docs/mandate/review-report.md`
- `README.md`
- `docs/mandate/security-report.md`
- Test suites: `UrlValidatorTests.cs`, `CodeGeneratorTests.cs`, `LinkRepositoryTests.cs`,
  `LinkServiceTests.cs` — each appearing twice in the artifact list with different byte sizes
- `docs/mandate/test-report.md` — appearing twice with different byte sizes

## Retries

- Number of stage retries: **not available**. The run context does not record a retry count
  for any stage. The presence of two differently-sized versions of each test suite file and
  of the test-report file is consistent with either a retry/re-run of the test stage or an
  intentional revision, but the run context does not distinguish between these, so no retry
  count is reported.

## Human interventions

- Number of human interventions during the run: **not available**. The run context records
  only that the run was initiated by `human:muskan` (`run.initiated-by`). It does not record
  any subsequent in-run intervention events, approvals, or edits, so none are reported here
  beyond the initiating request.

## Cost

- Stage-level cost, token cost, or time cost: **not available**. No cost or duration figures
  for any stage were supplied in the run context.

## Quality figures actually recorded

- `requirements.acceptance-criteria`: 20
- `requirements.ambiguity-score`: 0.3
- `implementation.files-changed`: 8
- `review.findings`: 2
- `review.highest-severity`: medium
- `security.findings`: 0
- `security.secrets-found`: false
- `test.failures`: 0
- `test.coverage`: 0.8259

## What this report does not claim

This report does not infer a number of retries or human interventions from the presence of
duplicate test-suite/test-report artifacts, since the run context gives no explicit count or
timestamp data to support such an inference. An approver who needs those figures for audit
purposes should request them from the run's execution log directly, as they are not present
in what was made available to this stage.