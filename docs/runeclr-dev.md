# RuneCLR development integration branch (`runeclr/dev`)

This branch is RuneCLR's long-lived runtime integration line. It is published in the
fork **ReneeGA2020/runtime**; Microsoft's **dotnet/runtime** remains the upstream.
The fork's default branch keeps tracking upstream and is not modified by this policy.

## Baseline

- Initial commit: 8b085f4aaf7a0063ea5512963525e5d70f6a450a (merge commit).
- Parents: 7cbdad39669188c1f49f0a41216829d9a9005591 (runeclr/reflection-policy-20261027)
  and 275ff40cbf142e7eb9a39acff98a6242a0b83519 (dotnet/runtime main, 2026-11-10).
- Content: the reflection/session-policy work plus that upstream snapshot. It does
  **not** include the later instruction-meter, terminal-prototype, batch-interval,
  mobile or callback-epoch patch stacks; those remain separate experiments.

## Private callback-epoch increment (2026-11-12)

The optional callback-deadline experiment is published separately as
`runeclr/optional-callback-epoch-integration-final-20261112`, a documentation-only descendant of
tested code commit `e637bd791bd824b3a8118fca47073f6390bcb52a` (measured against `runeclr/dev` at
`474eb8ad`). It is **not** promoted into `runeclr/dev` and does not enable deadlines by default: the clock
path stays the default, the interpreter checkpoint only charges a configured callback, and the
runtime owns no timer or thread. The private version-1 ABI, the configure-before-first-execution
contract, the recorded Windows x64 evidence and the limits are in
[the callback-epoch document](runeclr-callback-epoch.md). The initial baseline recorded below
stays the historical record for this branch.

## Cadence

Merge Microsoft upstream **every two weeks, Monday 10:00 Asia/Shanghai**. The
schedule is owned by an external automation task; this document only defines the
policy it must follow.

## Upstream tracking until .NET 12 GA

- While dotnet/runtime **main** still targets .NET 12, merge main.
- After main moves to .NET 13, verify the official .NET 12 release branch from
  Microsoft releases/tags and track that branch instead of main.
- Stop condition: when the official .NET 12 GA release/tag is confirmed, perform
  one final upstream sync and verification, then retire the periodic task.

## Merge and promotion rules

- Prepare each merge in an isolated candidate checkout (branch and worktree), then
  build and run the positive regression set before advancing `runeclr/dev`.
- Only advance the remote branch with a non-force push after the gates pass; on any
  failure leave the remote branch unchanged and keep the candidate for diagnosis.
- Runtime and crossgen2 outputs must be produced and paired from the same merged
  source; do not mix artifacts from different builds.
- Preserve RuneCLR bug-fix patches (including the known open upstream issues) until
  official fixes are published, verified locally and retested.
- Report desktop build/test evidence and any platform/device validation separately;
  a Windows result does not establish iOS or device capability.

## Verification recorded for the initial baseline

- Windows x64 Release `clr+libs` build with `FEATURE_INTERPRETER=1` (0 warnings/errors).
- RuneCLR `bdbc7f5` runtime suite: 156/156 baseline and 156/156 with
  `--require-interpreter`.
- Reflection/session policy suite with `--require-private-runtime`: PASS, 123 cases.
- `RuneCLR.SampleHost --collect`: hello map completes, entities released, unload collected.
- Upstream focused `JIT/Interpreter` case: \"All tests passed successfully!\" on this runtime.

Assisted-by: Cindy (https://cindy.app)
