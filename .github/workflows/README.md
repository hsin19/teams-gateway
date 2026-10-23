# CI/CD Workflows

## Architecture

```
check.yml    push to main · workflow_call · workflow_dispatch
             → build · unit tests · functional tests (SQLite, Postgres) · shellcheck
pr.yml       pull_request → check.yml · dependency review → auto-merge (Dependabot)
release.yml  v* tag       → check.yml → build images (.NET SDK) → smoke check → push amd64 + arm64 to GHCR
```

`check.yml` is the single definition of "did this tree pass". Both other workflows call it, so a
PR and a release are held to the same bar and the list cannot drift between them. A check belongs
there or nowhere. `main` has no deploy workflow of its own, so `check.yml` also triggers on a push
to `main`: that run is what holds a direct push, or a Dependabot merge, to the same bar as a PR.

Each suite is its own step, conditioned on `!cancelled()` and a successful build, so a single run
reports every failure instead of stopping at the first, and a compile error doesn't bury the real
cause under a wall of red steps.

The Postgres functional tests start a container through Aspire (`GATEWAY_TEST_DB=postgres`). The
runner provides Docker, and DCP comes from NuGet (`AspireUseCliBundle=false` in the test project),
so there is no Aspire CLI install step.

The E2E tests (`tests/TeamsGateway.Gateway.E2E`) are deliberately absent: they log in to a real
Teams account and send real messages.

Workflows are named for the event that starts them rather than `ci.yml`: CI is not what tells them
apart, since they all run the same gate.

## Release

Push a tag: `git tag v1.2.3 && git push origin v1.2.3`. The images are
`ghcr.io/<owner>/teams-gateway` and `ghcr.io/<owner>/teams-slack-relay`, tagged `1.2.3`, `1.2` and
`sha-<short sha>`.

- There is no Dockerfile: `dotnet publish -t:PublishContainer` builds the images from the project
  settings (`services/Directory.Build.props` and each csproj; see ADR 0010) and pushes them straight
  to the registry, so the workflow uses no Docker actions.
- Every tag is an image index holding `linux/amd64` and `linux/arm64`, both built on the x64
  runner without emulation: the app is IL, and only NuGet's native libraries differ per
  architecture. This works on a private repository too, unlike the free arm64 runners. The SDK
  pushes each architecture first under `<tag>-linux-x64` / `<tag>-linux-arm64`, so those tags show
  up in GHCR as well; deployments use the plain ones.
- The x64 image is built into the runner's engine and smoke-checked before anything is pushed: the
  gateway must answer `/health` with no secrets configured, and the relay must still be running
  after a few seconds. The gateway gets a `/data` owned by uid 1654, as a deployment must provide.
  The arm64 image cannot run here without QEMU, so it is pushed unrun.
- A newly pushed GHCR package is private by default. Make it public once in the package settings,
  or grant the deploying repository read access instead. The `org.opencontainers.image.source`
  label links the package to this repository.

## Dependency review

`pr.yml`'s `dependency-review` job fails every PR with "Dependency review is not supported on this
repository" until the **Dependency graph** is on. Enable Dependabot alerts (Settings → Advanced
Security), which turns it on too. The job also gates `auto-merge`, so with the graph off no
Dependabot PR merges.

## Dependabot auto-merge

`pr.yml` squash-merges a green Dependabot PR using `secrets.AUTOMERGE_TOKEN`, a dedicated token
rather than the default `GITHUB_TOKEN`: a push made with `GITHUB_TOKEN` never starts another
workflow, so `check.yml` would not run on the merged `main`. To set it up:

1. Create a fine-grained personal access token for this repository only, with **Contents** and
   **Pull requests** set to read and write.
2. Add it as a **Dependabot** secret named `AUTOMERGE_TOKEN` (Settings → Secrets and variables →
   Dependabot). Dependabot PRs read the Dependabot secret store, not the Actions one.

Before merging, the job checks that `main` hasn't moved since the check ran. If it has, it asks
Dependabot to rebase instead, and the rebased PR goes through the gate again.

Every action `release.yml` uses also runs in `check.yml`, so a green PR covers a bump to any of
them. Keep it that way: an action used only on a tag would first run during a real release, and
would need its own Dependabot group kept out of auto-merge.

The base images are not pinned and need no Dependabot entry: `noble-chiseled` is a floating tag,
so each release picks up the latest patched runtime.
