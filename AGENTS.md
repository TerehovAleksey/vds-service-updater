# Agent Guide

## Project overview

VDS Service Updater is an ASP.NET Core webhook service that updates container image tags in configured Docker Compose or Swarm YAML files, applies the deployment, waits for readiness, and rolls back on failure.

## Architecture

- `src/VdsServiceUpdater/Program.cs` configures the app, options, middleware, and API endpoints.
- The deploy flow validates and authorizes the requested image, scans configured targets, edits matching YAML image tags, applies deployments, and coordinates backups and rollback.
- YAML parsing is for discovery and verification only. Preserve the original file by changing only the matching image value; retain comments, quoting, whitespace, line endings, BOM, and unrelated content. Reject layouts that cannot be edited safely.
- Docker operations must go through the process-runner abstraction with argument lists, never shell interpolation.
- The API is synchronous. Deployment work is not cancelled when the HTTP client disconnects.

## Development

- Target framework: .NET 10.
- Build: `dotnet build VdsServiceUpdater.slnx --configuration Release`
- Tests: `dotnet test --configuration Release`
- Unit and API integration tests use a fake Docker process runner and do not need Docker.
- End-to-end tests use a real Docker daemon: `bash e2e/compose.sh` and `bash e2e/swarm.sh`. The Swarm test may initialize or leave Swarm on the host.

## Important constraints

- Keep configuration under the `Updater` section. Sources are `appsettings.json`, optional `/config/updater.json`, then environment variables; use `__` for nested environment keys. Avoid splitting indexed lists such as `Targets` across sources.
- Preserve the API's structured result/error contract and HTTP status semantics.
- Sanitize untrusted values before logging; never log webhook tokens or commit secrets.
- The updater controls Docker through the Docker socket, which grants host-level access. Do not expose it publicly; use HTTPS, strong authentication, and network restrictions.
- Keep the updater itself out of its deployment targets; updating it must be done manually.

See `README.md` for complete setup, configuration, API, deployment, and CI/CD documentation.
