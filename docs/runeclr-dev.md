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
  mobile or callback-epoch patch stacks; those remain separate experiments. This
  bullet describes the initial commit only; the 2026-11-13 merge `b747ab4d` later added the
  foundational opt-in callback-deadline increment described below.

## Foundational callback-epoch increment (merged 2026-11-13)

The opt-in interpreter callback deadline is part of this branch as of merge commit
`b747ab4d51d8da563b709a8b08ba69d15a314c03` (Merge pull request #2: opt-in interpreter callback
deadline, combined with session-origin dev). It stays **opt-in**: the clock path remains the
default, configuration must happen on a fresh interpreter-required context before its first
execution, and the runtime owns no timer thread of its own. The `InterpExecMethod` checkpoint only
charges a configured callback. The private version-1 ABI, the configure-before-first-execution
contract, the recorded Windows x64 evidence and the limits are in
[the callback-epoch document](runeclr-callback-epoch.md).

The earlier publication branch `runeclr/optional-callback-epoch-integration-final-20261112` (tested
code `e637bd791bd824b3a8118fca47073f6390bcb52a`, measured against the initial baseline at `474eb8ad`) remains
the historical reference for that increment. The initial baseline recorded below is kept as the
historical record; it describes the initial commit, not the current tip.

Large-interval optimization experiments are archived on two separate experiment branches and
are **not** merged into this branch. See
[the large-interval epoch record](runeclr-epoch-large-interval-20261114.md).

## 2026-11-16 upstream sync

The 2026-11-16 merge advanced the Microsoft upstream pin from
`275ff40cbf142e7eb9a39acff98a6242a0b83519` to
`a33eebbe8e6cc76c22f30ac9b9f166ac6469d088` at merge commit
`98f6629cb103f7dd398d8514d80137b2342dc07e` (parent `bcefcb77`).
The merge-triggered follow-ups are the QCall ordering fix
`6ed455709e7` and this documentation update.

Upstream split `src/coreclr/vm/qcallentrypoints.cpp` into separate entrypoint
tables. The five RuneCLR QCall entries were moved into the new
`src/coreclr/vm/runtimehelpers.cpp` table. That table binary-searches by
`strcmp`, so the entries are kept in sorted order; an earlier placement
caused a startup access violation and is retained only as the reason for the
follow-up fix. Both sides also changed the JIT-EE interface, so the merged
interface uses the new GUID
`9d56111e-110b-4d3e-9866-e1950fafb83a`.

Windows x64 Release verification on the merged source:

- `./build.cmd -subset clr+libs -configuration Release -arch x64 -clrinterpreter`:
  0 warnings, 0 errors.
- `src/tests/build.cmd -arch x64 release GenerateLayoutOnly`: Core_Root
  generated; its runtime binaries matched the private testhost layout.
- `RuneCLR.Runtime.Tests`: **157/157** ordinary and **157/157** with
  `--require-interpreter`; the required lane reported
  `SESSION_EXECUTION_POLICY required-interpreter version=1`.
- `RuneCLR.ReflectionPolicy.Tests --require-private-runtime`: PASS,
  `cases=123`.
- `RuneCLR.SampleHost --collect`: three contexts collected; the late-built
  map collected in both ordinary and required lanes.
- Upstream `JIT/interpreter/InterpreterTester`: Expected 100 / Actual 100,
  PASSED.
- Session-origin fixed lane (`--expect-fixed --require-origin` with the
  origin-scan switch enabled): `fixedModeObserved=true`,
  capability `1`.

This is Windows x64 Release compatibility evidence only. Release does not
provide the Debug-only interpreter dump/list diagnostics, so this record does
not independently prove per-method interpreted entry. It makes no iOS,
physical-device, mobile, R2R composition or performance claim.

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
