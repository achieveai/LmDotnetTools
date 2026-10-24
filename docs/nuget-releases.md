# Automatic NuGet releases

`.github/workflows/ci.yml` publishes the `AchieveAi.LmDotnetTools.*` packages after its
validation jobs pass. It uses the existing nuget.org trusted publisher for `ci.yml`,
`NuGet/login@v1`, and the repository's `NUGET_USER` variable. Do not rename that workflow
without updating its trusted-publishing policy.

## Version allocation

`Directory.Build.props` records the last published baseline, **1.0.33**, and
`ReleaseBaseCommit`, **c92bd0ed6721f2847fc8bea78d76aa4e0484cc36**. That commit had not
published any package newer than 1.0.33. The former props value 1.0.39 was an unpublished
manual value, not the release baseline.

Every first-parent `main` commit after the anchor adds one to the baseline patch:

- First main change: `1.0.34`.
- Second main change: `1.0.35`.
- A merge containing several feature commits still advances one patch.
- Rerunning the same commit reuses its version. A failed run can leave a version gap;
  a retry can finish a partially published version using `--skip-duplicate`.

This calculation depends on committed Git history, not the order that CI jobs finish or
the state of the NuGet feed. Feature PRs must not bump the baseline or anchor. Their
preview version uses the target main commit plus one patch, suffixed with
`-pr.<PR number>.<run number>`. Only a main push or a matching release tag can publish.
Manual workflow runs on main produce `-ci.<run number>` artifacts without publishing.
A manual run targeting a matching main release tag retains that tag's package version,
but still only uploads artifacts and never publishes.

The packages checkout fetches full history; a shallow checkout, missing anchor, or anchor
reachable only through a second parent fails closed. A `v<version>` tag must match the
computed version and point to a commit on main's first-parent history. It retries that
release, rather than allocating another version.

## Local builds and release evidence

Local builds use the props baseline. To inspect a CI version, run:

```powershell
./scripts/Get-PackageVersion.ps1 -EventName push -Ref refs/heads/main -Commit origin/main
```

CI passes the resulting major, minor, patch and preview label as global MSBuild
properties, so package versions, file versions and internal package dependency versions
agree. Changing the release series requires deliberately selecting a new already-published
baseline and its main commit together; ordinary PRs need no version-file edits.

Each workflow summary records its allocated version. PR runs upload `nuget-packages`
artifacts; inspect their `.nuspec` files to verify versions without publishing. A successful
PR pack proves packaging only. A successful main `Push to nuget.org` step and the packages
appearing in the NuGet feed establish live publication.

The push script enumerates individual `.nupkg` files, including paths with spaces,
rejects empty output and stops on the first native failure. Adjacent `.snupkg` files are
handled by `dotnet nuget push`. It does not pass a literal Windows wildcard to NuGet.
