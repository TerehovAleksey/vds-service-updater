# VDS Service Updater

Webhook service for deploying container images from CI/CD. Send an image tag to update matching services in configured Docker Compose or Docker Swarm YAML files. The updater makes a targeted edit, applies the deployment, waits for readiness, and restores the previous file and images if deployment fails.

## Features

- Docker Compose deployments and Docker Swarm service updates.
- Dry-run requests, per-file deployment locks, backups, and automatic rollback.
- Optional per-target cleanup of image references after successful deployment or rollback.
- YAML edits that preserve comments, quotes, whitespace, line endings, BOM, and unrelated content.
- Webhook token authentication, image allow-lists, request-size and rate limits, and trusted-proxy support.
- Multi-architecture container image for `linux/amd64` and `linux/arm64`.

## Requirements

- Docker Engine with the Compose plugin. The updater container must be able to access the Docker socket; Swarm targets require a manager node.
- A Docker registry login on the host where the updater runs. Mount a Docker `config.json` without a `credsStore` entry so Docker can use it in the container.
- Compose or stack YAML files accessible to the updater at the same absolute paths used in its configuration.
- .NET 10 SDK to build and run from source.

## Quick start

1. Create `updater.secrets.json` with a long random token:

   ```bash
   printf '{"Updater":{"Auth":{"Token":"%s"}}}\n' "$(openssl rand -hex 32)" > updater.secrets.json
   chmod 600 updater.secrets.json
   ```

2. Copy [`deploy/compose.example.yml`](deploy/compose.example.yml) and adjust the image allow-list, proxy address, target file, protected services, and host volume paths. The example mounts the secret configuration at `/config/updater.json`, Docker socket, registry credentials, target files, and persistent backup storage. Keep the updater's own stack out of its target list; update the updater manually.

3. Start the service:

   ```bash
   docker compose -f compose.yml up -d
   curl -fsS http://127.0.0.1:8080/health
   ```

   Expose the API only through a trusted HTTPS reverse proxy or a private network. See [`deploy/nginx.example.conf`](deploy/nginx.example.conf) for an nginx example and [`deploy/stack.example.yml`](deploy/stack.example.yml) for Swarm.

4. Test with a dry run, then deploy:

   ```bash
   curl -sS http://127.0.0.1:8080/api/v1/deploy \
     -H 'Content-Type: application/json' \
     -H 'X-Webhook-Token: YOUR_TOKEN' \
     -d '{"image":"ghcr.io/myorg/app:1.4.2","dryRun":true}'
   ```

   Set `dryRun` to `false` (or omit it) to apply the change. The response is synchronous and includes per-target and per-service results.

## Configuration

Configuration is read from `appsettings.json`, optional `/config/updater.json`, then environment variables; later sources override earlier ones. Use `__` for nested environment-variable keys, for example `Updater__Auth__Token`. Invalid settings fail startup. Do not split the same indexed list (such as `Targets`) across sources: entries are merged by index.

The complete example is [`deploy/updater.example.json`](deploy/updater.example.json). Settings are under the `Updater` section:

| Setting | Purpose |
|---|---|
| `Auth:Token` | Shared `X-Webhook-Token`. At least 16 characters when set; an empty value disables authentication, so do not expose the service without a token or an equivalent network control. |
| `Deploy:AllowedImagePrefixes` | Required allow-list. Requests outside these repository prefixes are rejected. Example: `ghcr.io/myorg/`. |
| `Deploy:AllowLatestTag` | Allow the mutable `latest` tag; defaults to `false`. |
| `Deploy:WaitTimeoutSeconds` | Deployment time budget in seconds (`10`–`3600`; default `120`). |
| `Deploy:StabilitySeconds` | How long Compose containers without a health check must remain stable (`0`–`300`; default `10`). |
| `Deploy:RollbackOnFailure` | Restore the previous YAML and reapply previous images when deployment fails; defaults to `true`. |
| `Deploy:BackupDirectory` | Absolute path for backups; default `/data/backups`. Mount persistent storage here. |
| `Deploy:BackupsToKeep` | Number of backups retained per target; minimum `1`, default `20`. |
| `Proxy:KnownProxies` | Trusted proxy IP addresses. Only trusted proxies may supply forwarded client IP/protocol headers. |
| `Proxy:KnownNetworks` | Trusted proxy networks in CIDR notation. |
| `Security:RequestsPerMinute` | API request limit per client IP (`1`–`6000`; default `30`). |
| `Security:MaxBodyBytes` | Maximum request body (`256`–`65536` bytes; default `4096`). |
| `Targets` | Deployment targets; at least one is required. See below. |

Each target has a unique `Name` (letters, numbers, `.`, `_`, `-`; max 64 characters), `Type` (`Compose` or `Stack`), an absolute `File` path, optional `StackName`, and optional `Protected` service-name glob patterns. Protected patterns are case-insensitive. Set `CleanupOldImagesAfterSuccess` and/or `CleanupNewImagesAfterRollback` to `true` to enable per-target best-effort cleanup (both are disabled by default). After a successful deployment the first option attempts to remove the previous image references for services changed by the request; after a successful rollback the second attempts to remove the requested/new references instead. It invokes only `docker image rm -- <exact-reference>`, without force, and never runs `docker image prune`. Docker may refuse removal when an image is still used by a container or another tag; cleanup failures are reported but do not change deployment status. In Swarm, removal applies only to the daemon configured for the updater; it does not attempt cleanup on worker nodes. For `Stack` targets, `StackName` is required. `ApplyMode` for Swarm is `ServiceUpdate` by default; `StackDeploy` deploys the entire stack from the YAML file and is experimental.

Example target settings in environment variables:

```yaml
Updater__Targets__0__Name: main
Updater__Targets__0__Type: Compose
Updater__Targets__0__File: /opt/stacks/main/compose.yml
Updater__Targets__0__StackName: main
Updater__Targets__0__Protected__0: postgres*
Updater__Targets__0__Protected__1: "*-db"
Updater__Targets__0__CleanupOldImagesAfterSuccess: true
Updater__Targets__0__CleanupNewImagesAfterRollback: true
```

For Compose, `StackName` sets the Compose project name (`-p`); if omitted, Docker derives it from the file's directory. For Swarm, `StackName` is the deployed stack name.

## API

### `POST /api/v1/deploy`

Headers:

- `Content-Type: application/json`
- `X-Webhook-Token: <token>` when authentication is enabled

Body:

```json
{
  "image": "ghcr.io/myorg/app:1.4.2",
  "dryRun": false
}
```

The image must include a tag; digests are not accepted in the request. The updater normalizes image references (for example, `nginx:1` and `docker.io/library/nginx:1` refer to the same repository), finds every matching service across configured targets, and updates allowed, unprotected matches. If `dryRun` is `true`, the service returns the planned results without locking files or changing them.

| HTTP status | Meaning |
|---|---|
| `200` | Deployment succeeded, the image was already current, or the request was a dry run. Inspect `status` and individual results. |
| `400` | Invalid JSON or request body. |
| `401` | Missing or invalid token. |
| `403` | Image is not allowed, or all matching services are protected. |
| `404` | No matching service was found. |
| `409` | Another deployment is already working on a target file. |
| `413` | Request body exceeds `Security:MaxBodyBytes`. |
| `422` | Invalid or untagged image, forbidden `latest`/digest, or matching image in YAML cannot be safely updated (for example, variable-tagged or digest-pinned image). |
| `429` | Client IP exceeded the API rate limit. A `Retry-After` header may be present. |
| `500` | Deployment failed, or rollback failed. A failed rollback requires manual intervention. |
| `504` | Deployment timed out. |

Successful response:

```json
{
  "status": "updated",
  "results": [
    {
      "target": "main",
      "service": "app",
      "status": "updated",
      "previousImage": "ghcr.io/myorg/app:1.4.1",
      "newImage": "ghcr.io/myorg/app:1.4.2"
    },
    {
      "target": "main",
      "service": "app-worker",
      "status": "skipped",
      "code": "protected",
      "previousImage": "ghcr.io/myorg/app:1.4.1",
      "message": "Сервис защищён от обновления."
    }
  ],
  "cleanup": [
    {
      "target": "main",
      "image": "ghcr.io/myorg/app:1.4.1",
      "attempted": true,
      "success": true
    }
  ],
  "durationMs": 8123
}
```

The optional `cleanup` array is present only when cleanup was enabled and at least one image-removal attempt occurred. Each item identifies the target and exact image reference, records whether removal was attempted/succeeded, and includes `error` on failure. Top-level `status` is `updated`, `unchanged`, `dry_run`, `failed`, or `error`. Service result statuses include `updated`, `unchanged`, `planned`, `skipped`, `failed`, `rolled_back`, and `rollback_failed`. Target read errors are reported in `warnings`; a target that cannot be read does not prevent other targets from being processed. Responses include `X-Request-Id`, which is also logged as `RequestId`.

### `GET /health`

Unauthenticated health endpoint:

```json
{ "status": "ok", "version": "1.0.0" }
```

## Deployment behavior and limits

- The YAML is parsed for service discovery, but is not serialized back. Only the image value on a supported `image:` line is changed; comments, quotes, whitespace, BOM, line endings, and other content are preserved.
- The repository spelling already in the file is kept (`nginx:1.0` becomes `nginx:1.27`). An image without a tag receives the requested tag.
- Matching is by normalized repository, ignoring tag. Every matching unprotected service is updated.
- Services matching `Protected` patterns, using a variable in the image tag (such as `app:${TAG}`), or pinned to a digest are skipped. A matching request with only variable-tagged or digest-pinned services returns `422`; if all matches are protected, it returns `403`.
- Unsafe YAML layouts are rejected rather than guessed: examples include flow-style service mappings, quoted `image` keys, YAML anchors/aliases on the image value, block scalars, and multiple YAML documents. YAML merge keys are not expanded for discovery, so an image supplied only through a merge key is not matched.
- The file is backed up before an atomic replacement. The updater checks that the file has not changed since it was read; if it has, the write is rejected. Keep the backup directory persistent. Replacing a file via rename may change its owner to the updater process user.
- Targets are processed sequentially. A failure in one target does not undo a successful deployment in another target.
- Compose runs `docker compose pull` and `docker compose up -d --no-deps --no-build` for affected services. Services must remain healthy, or stable for `StabilitySeconds` when there is no health check. Containers that exit successfully as one-shot jobs are not supported.
- Swarm `ServiceUpdate` runs `docker service update` with registry auth and rollback-on-failure. `StackDeploy` runs `docker compose config` and deploys the full stack; this mode is experimental and depends on the Docker CLI/Compose version.
- Reusing a tag after pushing different image contents is not reliable: Docker may not pull the new contents. Prefer immutable image tags.
- Deployment is synchronous. `WaitTimeoutSeconds` is the overall budget for targets in a request; rollback receives its own timeout budget. Client disconnects do not cancel an in-progress deployment. On application shutdown, the updater attempts to restore a changed file.
- If nginx or another proxy fronts the service, set its read timeout above `WaitTimeoutSeconds`, with extra time for rollback (up to roughly twice the timeout). A proxy timeout may end the client request while deployment continues.

## Security and networking

- The updater controls Docker through `/var/run/docker.sock`, effectively granting host-level Docker access. Do not expose it directly to the public internet. Use HTTPS, a strong token, and preferably network restrictions or VPN.
- Keep `Auth:Token` in a Docker secret or a read-only mounted configuration file rather than environment variables, which may be visible in `docker inspect`. Generate a token with `openssl rand -hex 32`.
- `AllowedImagePrefixes` is required (deny by default). Prefix matching respects repository boundaries: `ghcr.io/myorg/` does not allow `ghcr.io/myorgevil/`. Prefixes without an explicit registry are interpreted as Docker Hub repositories.
- Rate limiting is applied by client IP before token authentication. Forwarded headers are trusted only from `KnownProxies` and `KnownNetworks`. Configure these with the address/subnet the updater actually sees for nginx; otherwise requests may share the proxy's rate-limit bucket.
- Mount Compose/stack files at the same absolute paths inside and outside the container. Mount the Docker socket, registry `config.json` read-only, and a persistent volume for `/data`.
- For Swarm, run one replica on a manager node. Swarm published ports are not bound to `127.0.0.1`; prefer an nginx service on the same overlay network or use host-mode publishing with a firewall.
- Keep the updater itself out of its target list. Updating the process that is applying a deployment is not supported.

## Build, test, and run

Run from the repository root:

```bash
dotnet build VdsServiceUpdater.slnx --configuration Release
dotnet test --configuration Release
dotnet test tests/VdsServiceUpdater.Tests --filter "FullyQualifiedName~ComposeImageEditorTests"
dotnet test tests/VdsServiceUpdater.Tests --filter "FullyQualifiedName~ComposeImageEditorTests.Changes_only_tag_and_keeps_comment"
dotnet run --project src/VdsServiceUpdater
docker buildx build --platform linux/amd64,linux/arm64 -t vds-service-updater .
```

The filtered commands run a test class and one test method, respectively. The xUnit unit and API integration tests use a fake Docker process runner and do not require a Docker daemon. End-to-end tests use a real Docker daemon and require `docker`, `curl`, and `bash`; the Swarm script may initialize or leave Swarm on the host:

```bash
bash e2e/compose.sh
bash e2e/swarm.sh
```

CI runs the unit/integration and e2e tests and builds both container architectures; see [`ci.yml`](.github/workflows/ci.yml).

## CI/CD integration

Ready-to-use examples:

- [`examples/deploy.sh`](examples/deploy.sh) — generic CI deployment script; requires `UPDATER_URL` and `IMAGE`, accepts `UPDATER_TOKEN`, `DRY_RUN`, and `TIMEOUT`.
- [`examples/github-actions-deploy.yml`](examples/github-actions-deploy.yml)
- [`examples/azure-pipelines-deploy.yml`](examples/azure-pipelines-deploy.yml)

The CI workflow runs for pull requests targeting `main`. It runs tests, end-to-end checks, and a multi-architecture image build without publishing it. The release workflow can be started manually from `main`; it publishes `linux/amd64` and `linux/arm64` images to the configured registry with a short commit-SHA tag and the moving `latest` tag. Dispatching the workflow from another branch will skip its jobs.

Configure these repository-level GitHub Actions settings before running the release workflow:

| Type | Name | Value |
| --- | --- | --- |
| Secret | `REGISTRY_USERNAME` | Username or service-account name for the container registry |
| Secret | `REGISTRY_TOKEN` | Registry access token with permission to push to the image repository |
| Variable | `CONTAINER_REGISTRY` | `docker.io` for Docker Hub; use the registry host when changing providers |
| Variable | `CONTAINER_NAMESPACE` | Docker Hub username or organization |
| Variable | `CONTAINER_IMAGE` | `vds-service-updater` |

The registry and image coordinates are configurable. When switching providers, set `CONTAINER_REGISTRY` and the namespace/repository variables to the new location, and update the registry credentials to a push-enabled account or token. Pull requests never receive registry credentials and never publish images. See [`ci.yml`](.github/workflows/ci.yml) and [`release.yml`](.github/workflows/release.yml) for the workflow definitions. Updating the updater service itself is a manual operation.

Published images include OCI metadata labels for the application title and description, GitHub owner as author/vendor, project/source/documentation links, version, full source revision, and build time. `com.github.issues` points to the repository's GitHub Issues page. No license label is emitted because the project license has not been declared.

## Deployment examples

- [Docker Compose](deploy/compose.example.yml)
- [Docker Swarm](deploy/stack.example.yml)
- [Full updater configuration](deploy/updater.example.json)
- [nginx reverse proxy](deploy/nginx.example.conf)
