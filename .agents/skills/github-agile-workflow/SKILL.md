---
name: github-agile-workflow
description: Manage GitHub issue-driven development from Project board status through branch, logical Conventional Commits, pull request, and status synchronization. Use when an agent is asked to take a GitHub task, implement it, commit it, or open/link a PR; do not use for unrelated Git or GitHub work.
---

# GitHub Agile Workflow

Use this skill for a complete issue-driven workflow. Keep the GitHub issue, local branch, commits, PR, and Project item linked throughout the task.

## Board model

Prefer GitHub Projects v2 with a `Status` field containing exactly these logical states:

`Backlog` → `Todo` → `In Progress` → `Done`

Treat the states as follows:

- `Backlog`: identified work that is not yet selected.
- `Todo`: ready for an agent or developer to take.
- `In Progress`: a branch is being worked on or an associated PR is open.
- `Done`: the PR has been merged and the issue is closed or otherwise verified complete.

When project automation is available, configure it for issue creation, PR opening, and PR merge. Do not assume that closing an issue alone means the Project item moved to `Done`; verify the item status. If automation is absent, update the Project item explicitly through GitHub MCP/API and report failures.

## Starting a task

When asked to take an issue:

1. Read the issue title, body, acceptance criteria, labels, dependencies, and existing discussion. Confirm that it is actionable and in `Todo` (or obtain explicit user direction for another status).
2. Inspect the repository state and current branch. Preserve unrelated user changes; do not reset or overwrite them.
3. Derive a short, lowercase branch slug from the issue. Use `feat/<number>-<slug>` for a feature, `fix/<number>-<slug>` for a bug, and an appropriate type such as `docs/`, `refactor/`, or `chore/` otherwise. Example: `feat/123-oauth2-provider`.
4. Create the branch from the intended base branch, normally the repository default branch, and move the Project item to `In Progress`.
5. Implement and test the issue. Keep the work limited to its acceptance criteria.

If the issue is ambiguous, blocked by another issue, or lacks required access, stop before making speculative changes and report the blocker.

## Commit rules

Before committing, inspect `/diff` (the complete intended diff, including staged and unstaged changes as applicable) and group changes by logical implementation step. One logical step is one commit. Do not make a single catch-all commit when the diff contains independent steps, and do not split one inseparable step into artificial commits.

Use Conventional Commits:

```text
<type>(<scope>): <imperative subject>

<why, only when needed>

<footer references>
```

Allowed common types are `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `build`, `ci`, and `chore`. Use a scope when it makes the affected area clearer; omit it when it adds no information.

The subject must:

- use the imperative mood (for example, `Add OAuth2 provider`, not `Added OAuth2 provider`);
- be concise and no longer than 72 characters for the complete first line;
- avoid a trailing period;
- describe the result of the commit, not a vague activity such as `Update files`.

The body is optional and is used only to explain why the change was necessary, a non-obvious trade-off, or an important constraint. Do not use the body to enumerate what files or code were changed; that belongs in the diff. Keep each logical step reviewable and independently understandable.

Link the issue in the footer or body as appropriate:

```text
Closes #123
Relates to #456
```

Use `Closes`, `Fixes`, or `Resolves` when merging this PR should close the issue. Use `Relates to` when the issue must remain open. Put closing references in the PR description as well when repository conventions require it. Never claim an issue is fixed if the PR does not deliver its acceptance criteria.

Before each commit, check the staged diff, tests, subject length, imperative wording, and issue reference. Do not commit secrets, tokens, generated credentials, or unrelated changes.

## Pull requests

Push the working branch only after the intended commits and tests are ready. Open one PR from the task branch to the correct base branch. The PR should include:

- a concise Conventional Commit-style title describing the complete change;
- the linked issue and a closing keyword when appropriate (`Closes #123`);
- a short summary of the behavior or outcome;
- validation performed and any known limitations;
- relevant breaking-change or migration notes.

Use the repository PR template if one exists. Do not merge the PR unless the user explicitly asks for merging and the required checks/review policy permits it. After opening a PR, verify that it is linked to the issue and move the Project item to `In Progress` if needed. After merge, verify issue closure and move the Project item to `Done`; if either automation step failed, report it and make the authorized manual update.

## Status synchronization

Use this mapping as the default:

| Event | Project status |
|---|---|
| New issue | `Backlog` |
| Issue selected for work | `Todo` |
| Branch created / implementation started | `In Progress` |
| PR opened | `In Progress` |
| PR merged and issue completed | `Done` |

Do not move a task to `Done` merely because code was committed, pushed, or a PR was opened. Preserve the actual repository and Project state when reporting results.

## Tool and MCP expectations

The workflow needs access to:

- Git CLI (or an equivalent Git MCP) for branch, diff, commit, push, and status operations;
- GitHub MCP/API for issues, pull requests, and Project v2 items/statuses;
- repository test/build tools;
- credentials with the minimum required permissions.

Useful capabilities include `get_issues_by_status`, `create_branch`, `create_commit`, `push`, `create_pull_request`, `update_project_status`, and issue/PR lookup. Map these names to the tools actually available; do not invent successful operations. If GitHub MCP cannot update Projects, explain the missing capability and complete only the local work that is authorized.

## Safety and access

Use narrowly scoped credentials and repository access. Typical GitHub permissions are read/write access to repository contents, pull requests, issues, and only the required Project resources. Never expose tokens or credentials, commit them, or place them in logs. Do not grant or request organization administration when repository-level access is sufficient.

Before external mutations (push, issue/Project update, or PR creation), verify the target repository, issue number, branch, base branch, and intended change. Follow the user's authorization and repository policy; a request to implement an issue does not by itself authorize merging or broad administrative changes.

## Completion report

Report the issue number, branch, commits, tests/checks, PR URL, and final verified Project/issue status. Clearly separate completed actions from actions blocked by missing MCP capabilities, permissions, failed checks, or review requirements.
