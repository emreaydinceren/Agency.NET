# Agency.PackageSmokeTest

CI-only tool for **RT39** — not shipped, not part of `Agency.slnx`.

Proves a single package restores and its main assembly loads at runtime, without knowing
anything about that package's API. `ci-main.yaml`'s "Inspect & test-install published packages"
step copies this project to a scratch directory per package, `dotnet add package`s the
just-published package from the Gitea feed into the copy, then runs it:

```bash
dotnet run --project <scratch-copy>/Agency.PackageSmokeTest.csproj -- Agency.Configuration
```

Exit code 0 means the package installed and its assembly loaded cleanly; non-zero means a missing
dependency, a bad target framework, or some other packaging defect that `unzip -l` content checks
can't catch.
