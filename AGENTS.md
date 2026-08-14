# LLMW.Writing repository instructions

## Baseline and precedence

Before making a change, read the Product FROZEN, Architecture FROZEN, and Implementation Design. Apply this precedence without exception:

`Writing_Module_Requirements_Checkpoint_v0.5.2_FROZEN.md` > `Writing_Technical_Architecture_Spec_v0.1_FROZEN.md` > `Implementation_Design_v0.1.md` > ADR > local implementation detail.

The current checkout stores the Architecture FROZEN document as `docs/architecture/Writing_Technical_Architecture_Spec_v0.1_FROZEN(1).md`. Treat it as the Architecture FROZEN baseline; do not alter its content or infer a design change from its filename.

## Required rules

1. Do not alter frozen invariants without Architecture Review.
2. Modify only directories assigned to the active work package.
3. Run the required tests and report their results.
4. Do not add dependencies without human approval and an entry in `docs/implementation/dependency-register.md` covering purpose, license, security/update owner, and replacement boundary.
5. Never commit or push directly to master.
   For a dedicated work-package feature branch, an agent may create and push
   a checkpoint commit only when the active user instruction explicitly
   authorizes it and all required verification passes.
   Never amend/rebase accepted history or force-push.
6. Treat schema, IPC, FSM, and security-boundary changes as review-sensitive.
7. Preserve fault-injection hooks and test fixtures.
8. Keep the assembly boundaries intact: Domain and Contracts do not take UI, database, IPC, provider, Git, OpenXML, or Windows-sandbox dependencies; executable projects never reference another executable implementation.

## Work-package protocol

Inspect the repository, produce and validate the work-package plan against frozen invariants, modify only the allowed scope, run the required verification, and report changed files, dependencies, invariants, test evidence, risks, and unblocked packages.
