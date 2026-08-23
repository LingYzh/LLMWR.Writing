# LLMW controlled LibGit2Sharp native runtime

This is an internal, `win-x64`-only native asset package for the exact
`LibGit2Sharp.NativeBinaries` 2.0.324 dependency selected by LibGit2Sharp
0.32.0. It replaces the upstream 1.8.x payload with audited libgit2 1.9.6.

The package is resolved only by LLMW.Writing.Infrastructure. Its replacement
boundary is `IGitService`; Application, Domain, Contracts, and renderer code
do not depend on this package or its native ABI.

Source provenance and all native asset SHA-256 values are recorded in
`eng/native/LibGit2Sharp.NativeBinaries.audit.json` in the repository.
