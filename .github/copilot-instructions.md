# Copilot instructions

## Build and test

The solution targets .NET 10. Run these commands from the repository root:

```bash
dotnet build VdsServiceUpdater.slnx --configuration Release
dotnet test --configuration Release
dotnet test tests/VdsServiceUpdater.Tests --filter "FullyQualifiedName~ComposeImageEditorTests"
dotnet test tests/VdsServiceUpdater.Tests --filter "FullyQualifiedName~ComposeImageEditorTests.Changes_only_tag_and_keeps_comment"
dotnet run --project src/VdsServiceUpdater
```

The first filtered test command runs one test class; the second shows how to run one test method. The test project uses xUnit. Its API integration tests run the real ASP.NET Core pipeline with `WebApplicationFactory` and replace Docker process execution with a fake, so the normal test suite does not require Docker.

End-to-end tests use a real Docker daemon and require `docker`, `curl`, and `bash`:

```bash
bash e2e/compose.sh
bash e2e/swarm.sh
```

The Swarm script may initialize or leave Swarm on the host. CI runs `dotnet test --configuration Release` and both e2e scripts; see `.github/workflows/ci.yml`.

## Architecture

`Program.cs` composes the ASP.NET Core app: it loads and validates `Updater` options at startup, configures trusted forwarded headers, rate limiting and token middleware, and exposes `/health` and `POST /api/v1/deploy`.

The deploy request flows through these layers:

1. `ImageRequestValidator` parses and authorizes the requested image against configured prefixes.
2. `DeployOrchestrator` plans matching changes across configured targets, acquires per-file locks, coordinates backups and edits, validates the resulting compose file, applies the deployment, and restores/reapplies previous images on failure.
3. `ComposeFileScanner` uses YamlDotNet to discover services and image line numbers. `ComposeImageEditor` changes only the image tag in the original text and verifies the edited YAML; `ComposeFileStore` handles snapshots, backups, atomic writes, and restoration.
4. `ServiceDeployer` selects Compose or Swarm operations. `DockerCli` builds Docker CLI commands, and `ProcessRunner` executes them without a shell. `ReadinessWaiter` checks service health/update status before success is reported.

The API is synchronous: the response reflects deployment completion or failure. Deployment uses the application-stopping cancellation token, not the HTTP request token, so a disconnected client does not cancel work or rollback.

## Repository conventions

- Keep YAML parsing and YAML writing deliberately separate: use YamlDotNet for discovery/verification, but preserve the source file by replacing only the matching `image:` value. Preserve comments, quoting, whitespace, line endings, BOM, and unrelated content. Reject layouts that cannot be edited safely rather than serializing the full YAML document.
- Keep orchestration and rollback in `DeployOrchestrator`; Docker command details and readiness checks belong in the Docker layer. Invoke Docker through `IProcessRunner` with an argument list (never shell interpolation), bounded timeouts, and sanitized output.
- Bind runtime settings under the `Updater` section. Configuration precedence is `appsettings.json`, optional `/config/updater.json`, then environment variables (for example, `Updater__Targets__0__File`). Options are validated on startup. Avoid splitting the same indexed list across configuration sources because list entries can combine by index.
- Preserve the API's structured error/result contract and meaningful HTTP status codes. Sanitize untrusted values before logging; never log the webhook token.
- Keep tests close to the behavior they cover in the corresponding `*Tests.cs` files. Use the fake `IProcessRunner` for unit/integration coverage; reserve real Docker behavior for `e2e/`.
