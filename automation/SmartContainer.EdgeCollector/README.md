# Scheduled Microsoft Edge collector

This collector runs on a GitHub-hosted Windows machine. It uses the Microsoft
Edge installation already present on `windows-latest`; it does not install a
browser and it does not depend on the user's computer.

## GitHub repository secrets

Create these repository secrets under **Settings > Secrets and variables >
Actions**:

- `SMART_CONTAINER_IMPORT_URL`:
  `https://eshinetong.com/api/smart-containers/import`
- `SMART_CONTAINER_IMPORT_SECRET`: the same long random value configured as
  `SmartContainerImport__ApiKey` on the Linux backend.

Do not store either secret in source files or workflow logs.

## First test

Open **Actions > Smart-container Edge collection > Run workflow** and keep
`dry_run` enabled. The run must upload an artifact containing:

- `smart-container-snapshot.json`
- `worksheet-太仓.tsv`
- `worksheet-内河点.tsv`
- `wps-success.png`

Inspect the JSON before running the workflow again with `dry_run` disabled.
Scheduled runs always import and execute at 01:00 UTC, which is 09:00 in
Asia/Shanghai. GitHub may start scheduled jobs a few minutes late during busy
periods; the payload includes the actual collection timestamp.

The first push to `main` or `master` that adds or changes the collector runs
one immediate non-dry collection and imports it into MySQL through the backend.
Configure the backend, apply database script `007`, and create both repository
secrets before that push. Later code pushes that change the collector run the
same immediate integrity check and import.

## Local dry run for development only

The production workflow does not use the local computer. A developer can run
the same collector against an installed Edge without uploading data:

```powershell
$env:SMART_CONTAINER_DRY_RUN = "true"
dotnet run --configuration Release --project automation/SmartContainer.EdgeCollector/SmartContainer.EdgeCollector.csproj
```

The collector selects `A1:Z200` in each configured worksheet, copies the
tab-separated cells from WPS, parses the fields without OCR, validates the
result, and then sends only normalized JSON to the backend.
