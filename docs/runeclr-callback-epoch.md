# Optional callback deadline (private interpreter experiment)

Status: this is the publication branch for the Windows x64 callback-deadline experiment. The
deadline code under test is `e637bd791bd824b3a8118fca47073f6390bcb52a`; this document and the short
note in `docs/runeclr-dev.md` are the only commits on top of it, together with a merge of the
advanced `runeclr/dev` that now carries the opt-in session-origin change. The branch is measured
against `runeclr/dev` at `0c8c1a682e95c3c8ab57842ed19f7328c66ccc47`. It exposes no public API and
does not enable deadline charging by default; the merge decision is tracked in pull request #2,
not in this document.

## Contents

- Private QCall `AssemblyNative_RuneCLRDeadline` (operations 0-10) next to the existing version-1
  execution-policy QCalls, a private surface on `System.Runtime.Loader.AssemblyLoadContext`, and
  native state in `src/coreclr/vm/runeclrdeadline.h`.
- An interpreter checkpoint at dispatch. On Windows x64 Release the per-frame checked mode is
  folded into the central switch index, so an unbudgeted frame reaches its handler without a mode
  test. The backend reuses the existing per-frame deadline cache and the shared handler bodies: it
  adds a checked and an unchecked entry stub per opcode and does **not** duplicate an opcode body
  or introduce a second cache layer. Windows Debug/Checked, Wasm, non-Windows and computed-goto
  builds keep the previous backend.
- A self-contained fixture at `src/tests/JIT/interpreter/RuneCLR/CallbackEpochDeadline`
  (`CLRTestKind=BuildOnly`, `RequiresProcessIsolation`, no xunit wrapper). It is launched directly
  against a built Core_Root and exits 0 when every check passes (1 on a failed check, 2 from its
  watchdog).
- Against `0c8c1a68` the branch carries the 14-file deadline delta from `e637`, the merge commit
  that brings in the five session-origin files, and documentation commits; the pull request shows
  the current file and commit counts. The eleven deadline-only product files are byte-identical to
  `e637`; in the three files it shares with the session-origin change (`assemblynative.cpp`,
  `assemblynative.hpp`, `qcallentrypoints.cpp`) the additional lines are the merged session-origin
  additions.

## Private ABI (version 1)

`AssemblyNative_RuneCLRDeadline(binder, operation, value, interval)` returns `INT64`; without
`FEATURE_INTERPRETER` the QCall throws `PlatformNotSupportedException`. Version and capabilities
need no binder.

| operation | meaning | binder required |
| --- | --- | --- |
| 0 | ABI version, returns 1 | no |
| 1 | configure clock deadline (value = 100 ns, interval) | interpreter-required custom binder |
| 2 | begin callback, returns token | interpreter-required custom binder |
| 3 | end callback (owner + token) | binder that already carries a deadline |
| 4 | query diagnostic field | binder that already carries a deadline |
| 5 | configure epoch deadline (value = 100 ns, interval) | interpreter-required custom binder |
| 6 | declare the process-wide epoch period (microseconds) | no |
| 7 | tick the epoch, returns the new epoch | no |
| 8 | read the epoch | no |
| 9 | capabilities, returns 3 | no |
| 10 | set the cleanup budget | interpreter-required custom binder |

Capabilities are a bit set: **bit0 (0x1) is the version-1 clock deadline, bit1 (0x2) is the opt-in
epoch mode**; there is no `1 << 2` bit. The private managed helpers are
`RuneCLR_GetCallbackDeadlineVersion`, `RuneCLR_GetCallbackDeadlineCapabilities`,
`RuneCLR_CreateCallbackDeadline`, `RuneCLR_CreateCallbackDeadlineEpoch`,
`RuneCLR_BeginCallbackDeadline`, `RuneCLR_EndCallbackDeadline`,
`RuneCLR_QueryCallbackDeadline`, `RuneCLR_TickCallbackDeadlineEpoch` and
`RuneCLR_ReadCallbackDeadlineEpoch`. Operation errors surface as
`PlatformNotSupportedException` (missing API or binder without a deadline) and
`InvalidOperationException` (late configuration, missing interpreter policy, invalid begin);
invalid numeric arguments are `ArgumentOutOfRangeException`.

## Configure-before-first-execution contract

- Every configuration entry (clock deadline, epoch deadline, cleanup budget) takes the same
  `Fresh -> Configuring` compare-exchange before it writes any member, so there is exactly one
  writer per binder. Validation happens before the CAS; every post-CAS failure restores `Fresh`
  and a later configuration may retry. Members are only read after publication, and configuration-
  derived query fields read 0 while unpublished.
- The first interpreted dispatch claims the binder with a `Fresh -> Executing` compare-exchange.
  `Executing` is terminal: a later configure reports "can only be enabled before this load context
  first executes; create a new context instead". A losing frame re-reads the state, so a configure
  that races the first execution cannot be half-applied.
- Operations 1, 2, 5 and 10 first require `RuneCLRRequiresInterpreter()` on the binder, so a context
  whose code may already have run (for example under the JIT) can never be counted as "never
  executed". Operation 3 validates the owner and token through `End()`; operation 4 deliberately
  keeps the version-1 diagnostic contract (raw known fields, `-1` for unknown fields, no owner
  check and no throwing path inside the query itself; a binder without a deadline is still
  rejected before the call) and must not be guarded by a future consistency edit.
- `Begin` requires a published, enabled configuration, no recorded failure, a single-owner
  compare-exchange on the active flag, a non-saturated token and, in epoch mode, a live ticker
  (`epoch > 0` and not saturated). Every failure records a reason and releases ownership.
  `End` requires the same owner and token, polls once and releases. `Check` fails closed on a
  stale or absent owner and polls on boundaries or every opcode; a competing callback or thread
  fails closed with reason 4.
- Epoch mode is process-wide: the first declaration latches `epochPeriodMicros`, a conflicting
  value is rejected, and the runtime creates no timer and no thread. The trusted host ticks and
  reads the epoch and brackets every callback. A frozen or absent ticker stops advancing the epoch
  (a liveness gap, not a preemption); `Begin` refuses to start without a live epoch.
- The cleanup budget is a bounded number of cleanup charges that only applies after termination to
  the owning cleanup funclet; ordinary finally blocks of a running callback keep the normal check.

## Verification recorded for this candidate

| area | result | evidence |
| --- | --- | --- |
| combined cohort (deadline + session-origin, merge commit `234c0b7a90f`) | Release build 0/0; native 69/69 PASS; IL tail 5 assertions PASS; source checker 8/8; Windows Debug compile 0/0; both private APIs present (policy version 4, deadline version 1) | integration-merge-20261113/runtime/combined-*.log |
| combined delivery identity | coreclr 48289d63..., clrinterpreter 6b861fc7..., clrjit 197834f4..., corerun 30a8dd9a..., CoreLib e9a94ae0...; `SHA256SUMS.txt` 529a34b3...; `pdb/coreclr.pdb` bacbb515... | delivery-combined/SHA256SUMS.txt |
| fresh focused upstream on the combined cohort | `ContextualReflection`, `CollectibleStatics`, `GenericVirtualMethod` and `InterpreterTester` each expected 100 / actual 100, wrapper exit 0, run from already-built 474-based Checked test assemblies against the combined Core_Root | integration-merge-20261113/upstream/REPORT.md |
| host join on the combined cohort | epoch suite 24/24; lifecycle 156/156 ordinary and 156/156 with `--require-interpreter`; stock lifecycle 156/156; session-origin matrix 16/16 modes (710 pins, no drift, no failures) against an explicit `474eb8ad` base with the two documentation files allowlisted; provenance fixtures 43/0 and 11/0 | integration-merge-20261113/host/combined-receipt.json |
| native correctness cohort of the pre-merge deadline source | 69/69 PASS, exit 0 | final2-correctness.log |
| IL tail-call fixture | 5 assertions PASS | final2-tail.log |
| Windows Debug compile | 0 warnings, 0 errors; Checked backend selection has source proof only | guardfix-debug-build.log |
| source checker on e637 | 8/8 PASS; negative 41bc/1ac 6/8, S3 prototype 2/8 | phase2 verdict logs |
| delivery identity | coreclr 08e031af4e6fbd649dae4e30cb839d5a3d9b135f1a32629b34ac676f1dda806c, clrinterpreter f4d5ea48..., clrjit 4b15f3ae..., corerun 09017fa3..., CoreLib 30020d07... | delivery-1ac/SHA256SUMS.txt |
| host join on that delivery | epoch suite 24/24; lifecycle 156/156 ordinary and 156/156 with --require-interpreter; verifier 659 pins, 521-file layout, zero drift | final-hostjoin/ |
| focused upstream | ContextualReflection, CollectibleStatics, GenericVirtualMethod and InterpreterTester each Expected 100 / Actual 100, exit 0 | final-upstream/ |
| fixture packaging | project marked BuildOnly; measured fixture launched directly against the Core_Root, exit 0 | project file and native lane logs |

The rows above the combined row were recorded on the pre-merge deadline source (`08e031af`, delivery
`delivery-1ac`). The combined rows describe that source merged with the session-origin change, built
from `234c0b7a90f` into `delivery-combined`, and are the cohort proposed for `runeclr/dev`. The
focused upstream suite was re-run on that combined cohort in a separate workspace; this workspace's
own harness still stops at `MSB4184 ... ReadAllText('')` before it builds the tests, so the suite ran
from already-built assemblies rather than a rebuild.

The upstream `InterpreterTester` is the official wrapper and sets `DOTNET_Interpreter=RunInterpreterTests`
internally; it asserts only the child exit code, so this is compatibility evidence for the documented
configuration, not an independent per-method interpreted-mode trace. Native blocking cannot be
interrupted, and an author finalizer in a configured context is process-fatal (see *Author
finalizers in a configured context* below); the experiment is a fail-closed budget, not a sandbox.

## Artifact and build relation

The inspected Release build log (`final2-release-build.log`) stamps
`CLI_CMAKE_COMMIT_HASH=1ac56d5968106be661cc61b35cc1836301c14f9a` because that build ran before
`e637` existed. `e637` consolidates backend guards and updates comments and the test fixture.
Its active Windows Release interpreter implementation is identical to `1ac56d5` (3829 vs 3829,
0 differing lines); its separate Windows Debug compile closes the configuration break. Checked
backend selection is covered by source checks, not by a separate Checked build. The delivered
`08e031af` binary is therefore the measured artifact of this Release code path, not a build
produced by the documentation commit on this branch.

The combined cohort row above is a separate build of `234c0b7a90f` (coreclr `48289d63` with CoreLib
`e9a94ae0`), recorded in `delivery-combined` with `SHA256SUMS.txt` `529a34b3`.

## Performance scope

These are single-machine Windows x64 Release interpreter measurements, without CPU affinity.
The final three-round window compares an unconfigured patched runtime with unpatched `474eb8ad`:

| workload | final / baseline elapsed time |
| --- | --- |
| TightLoop | 1.03x |
| CallBoundary | 1.11x |
| BusinessLoop | 0.97x |

An earlier six-round window measured 0.96x / 1.04x / 0.95x. These windows support low default
overhead in the measured workloads, not universal zero overhead or a noiseless speedup.
With deadlines enabled, epoch poll intervals 1 / 100 / 1000 measured about **3.4x–5.3x** elapsed
time relative to the same build's OFF lanes. Boundary polls and per-opcode accounting remain;
the interval is not an instruction quota. The accepted roughly 2x deadline tolerance concerns
time-budget granularity and is not acceptance of CPU overhead.

A separate corrected cross-module delegate workload used three interleaved rounds. The median
of the per-round minimum-of-seven times was 14.6 ms for the baseline and 14.3 ms for the final
runtime; their sample-median aggregates were 16.8 ms and 14.6 ms. The original 13.9 ms / 13.9 ms
headline used minima across rounds. These are distinct statistics, not a universal speed claim.

## Reproduction

Build the runtime subset with the interpreter (Windows x64):

```
build.cmd -subset clr.runtime+clr.corelib+libs -c Release -arch x64 -clrinterpreter
build.cmd -subset clr.runtime -c Debug -arch x64 -clrinterpreter
```

Assemble a flat Core_Root from the native outputs plus the paired framework and run the fixture
directly (it is BuildOnly, so there is no generated xunit wrapper):

```
corerun.exe CallbackEpochDeadline.dll
```

The expected result is a final `PASS` line and exit code 0.

## Author finalizers in a configured context

An isolated artifact lane shows that this prototype cannot safely handle this ordinary author
mistake. In a context configured with a CLOCK deadline (60 s, poll interval 1, cleanup budget 1000),
an author object allocated inside `Begin`/`End` was kept alive through `End` and released
afterwards. Its finalizer then ran on the finalizer thread outside any active callback, was denied
at its first interpreter checkpoint, and the resulting unhandled `System.OperationCanceledException`
terminated the process (exit 0xE0434352). The finalizer wrote no marker at all, so the failure
happened before its first observable instruction; the first-checkpoint/owner-active explanation is
source reasoning, not a dynamically traced opcode. The same lane fails identically on the frozen
`7f5a0bc8` build and on the final `08e031af` build, while the same payload in a context without a
deadline runs both finalizer markers and exits 0. This is a pre-existing limitation, not a
regression of the G1/switch-index work, and it is not cleanup or recovery.

The lane does not establish how a finalizer behaves inside an active callback (that lane was not
run), does not provide a separate epoch-mode finalizer test, and does not show that every finalizer
in the process is blocked. It does mean a host must not keep pending author finalizers in a
budgeted context. Explicit disposal alone does not prevent finalization: a type whose design relies
on cleanup must either release its resources inside the active callback and leave no pending
finalizer, or suppress finalization correctly as part of that type's disposal design. Generic
`Dispose`/`using` or unloading the context is not a guarantee against this failure, and the
prototype cannot safely recover from it.

## Limits

- Windows x64 Release interpreter only. Linux computed-goto, Wasm and Windows Debug/Checked keep the
  previous backend; Debug is proven to compile, not to execute this backend.
- No Linux, Wasm, device, AOT, unmanaged-entry, global all-load or JIT/R2R instrumentation evidence.
- Invalid-raw opcode rejection has structural proof, not a live corrupted-bytecode test. There is
  no dedicated continuation or calli-group execution fixture; the tail-call and cross-context
  execution evidence should not be read as coverage of every opcode group.
- One active callback per session and a single owner thread; a competing callback or thread fails
  closed.
- Native blocking cannot be interrupted, including a `Sleep` inside a finally block. Author
  finalizers in a configured context are process-fatal (see above). This is a fail-closed budget,
  not a sandbox.
- The minimal epoch runtime has no instruction quota or terminal bridge; the both-feature host path
  is only covered at its pre-author missing-capability check.
- The host must keep the ticker alive and dispose it from an ordinary thread; the final join has no
  timeout and the trusted tick/read calls are assumed non-blocking.
