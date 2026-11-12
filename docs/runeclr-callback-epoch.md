# Optional callback deadline (private interpreter experiment)

Status: this is the publication branch for the Windows x64 callback-deadline experiment. The code
under test is `e637bd791bd824b3a8118fca47073f6390bcb52a`; this document and the short note in
`docs/runeclr-dev.md` are the only commits on top of it. The branch is measured against
`runeclr/dev` at `474eb8ad7230122cc0d34318b25d673da63fd138`. It is not merged into `runeclr/dev`,
exposes no public API and does not change any default path.

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
- The code commit changes 14 files against `474eb8ad` (+1870/-1). Every product blob on this
  publication branch is identical to `e637`; the branch adds documentation only.

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
| native correctness cohort | 69/69 PASS, exit 0 | final2-correctness.log |
| IL tail-call fixture | 5 assertions PASS | final2-tail.log |
| Windows Debug/Checked compile | 0 warnings, 0 errors | guardfix-debug-build.log |
| source checker on e637 | 8/8 PASS; negative 41bc/1ac 6/8, S3 prototype 2/8 | phase2 verdict logs |
| delivery identity | coreclr 08e031af4e6fbd649dae4e30cb839d5a3d9b135f1a32629b34ac676f1dda806c, clrinterpreter f4d5ea48..., clrjit 4b15f3ae..., corerun 09017fa3..., CoreLib 30020d07... | delivery-1ac/SHA256SUMS.txt |
| host join on that delivery | epoch suite 24/24; lifecycle 156/156 ordinary and 156/156 with --require-interpreter; verifier 659 pins, 521-file layout, zero drift | final-hostjoin/ |
| focused upstream | ContextualReflection, CollectibleStatics, GenericVirtualMethod and InterpreterTester each Expected 100 / Actual 100, exit 0 | final-upstream/ |
| fixture | CallbackEpochDeadline builds as BuildOnly and exits 0 against the Core_Root | native lane logs |

The upstream `InterpreterTester` is the official wrapper and sets `DOTNET_Interpreter=RunInterpreterTests`
internally; it asserts only the child exit code, so this is compatibility evidence for the documented
configuration, not an independent per-method interpreted-mode trace. Native blocking cannot be
interrupted; the experiment is a fail-closed budget, not a sandbox.

## Artifact and build relation

The inspected Release build log (`final2-release-build.log`) stamps
`CLI_CMAKE_COMMIT_HASH=1ac56d5968106be661cc61b35cc1836301c14f9a` because that build ran before
`e637` existed. `e637` itself changes only preprocessor guards and comments: its active Windows
Release lines are identical to `1ac56d5` (3829 vs 3829, 0 differing lines), and its separate
Windows Debug/Checked compile is what closes the configuration break. The delivered `08e031af`
binary is therefore the measured artifact of this Release code path, not a build produced by the
documentation commit on this branch.

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

## Limits

- Windows x64 Release interpreter only. Linux computed-goto, Wasm and Windows Debug/Checked keep the
  previous backend; Debug is proven to compile, not to execute this backend.
- No Linux, Wasm, device, AOT, unmanaged-entry, global all-load or JIT/R2R instrumentation evidence.
- One active callback per session and a single owner thread; a competing callback or thread fails
  closed.
- Native blocking cannot be interrupted, including a `Sleep` inside a finally block; finalizers run
  outside the deadline. This is a fail-closed budget, not a sandbox.
- The minimal epoch runtime has no instruction quota or terminal bridge; the both-feature host path
  is only covered at its pre-author missing-capability check.
- The host must keep the ticker alive and dispose it from an ordinary thread; the final join has no
  timeout and the trusted tick/read calls are assumed non-blocking.
