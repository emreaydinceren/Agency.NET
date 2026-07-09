# 1. No assembly strong naming

## Status

Accepted

## Context

Strong naming (`SignAssembly` / `AssemblyOriginatorKeyFile`) is a recurring
suggestion under the banner of "security hardening," but it doesn't do what
that framing implies:

- In an open-source repo the signing key is public (it has to be, for anyone
  to build the project), so anyone can strip and re-sign a strong-named
  assembly. It provides *assembly identity* — a stronger name than a bare
  simple name — not tamper protection.
- Its practical uses are the legacy GAC and the case where a *consumer's own*
  strong-named assembly needs to reference yours. Neither applies here: no
  GAC deployment, and no enterprise consumer has asked for a strong-named
  reference.
- It is a **one-way door**. Once a package ships strong-named, removing it is
  a breaking change for anyone who took a dependency on that identity.
- It adds ongoing `AssemblyVersion` / binding-redirect burden for every
  release, for a benefit nobody has asked for.

This is distinct from **package signing** — author-signing the `.nupkg`
itself — which *is* legitimate supply-chain hygiene, tracked separately
(source task RT38) and not yet decided. Strong naming and package signing
are unrelated mechanisms that get conflated because both involve a "key"
and the word "sign."

## Decision

Do not strong-name assemblies. No `SignAssembly` or
`AssemblyOriginatorKeyFile` properties are set anywhere in the build.

## Consequences

- No `AssemblyVersion` binding-redirect maintenance burden.
- Assemblies cannot be referenced by other strong-named assemblies or
  deployed to the GAC. Acceptable: no consumer has needed this.
- If an enterprise consumer ever provides a concrete need, this can be
  revisited — add a checked-in `.snk` (the public key isn't a secret, so
  committing it is safe) and pin `AssemblyVersion` to the major version only,
  so minor/patch releases don't break binding for existing consumers.
