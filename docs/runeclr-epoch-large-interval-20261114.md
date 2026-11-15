# Large poll-interval epoch experiments (archive record, 2026-11-14)

Two experiment branches exist for the large poll-interval work on the opt-in epoch callback
deadline. Neither is merged into `runeclr/dev`; both are separately published archival
branches (this documentation change prepares that push) so the S2 stop point and its evidence stay
reproducible.

## Branches

| branch | commit | parent | content |
| --- | --- | --- | --- |
| `runeclr/epoch-ref10k-20261114` | `6810f142b4e98d36f0b7ab98615e2a05bfdbab3b` | `ca8c85eb0948301a97c385aef94b46c94d127b9a` (C cohort) | only the two per-configuration 10000 allowlist checks (clock and epoch); default interval unchanged |
| `runeclr/epoch-large-interval-s2-20261114` | `64571e93fb27d7817f00786dd22d7f549ca7fa00` | `ead0248a86e2706af6ce4c1dff9f38578f7652a8` (S1) | validated-segment fast path plus the S2 codegen changes |

REF10K exists because the C cohort only accepts 1/100/1000, so its 10000 rejection is a
configuration-domain observation and not a reference result. REF10K differs from C only by the
allowlist; its parent is C, not `runeclr/dev`.

S2 adds, inside one verified live segment of the same frame, a per-frame budget and callback
token kept in the interpreter dispatch state, with a single grant issued by the shared
out-of-line helper after a successful check and only while the callback is active and in the
execution phase. Up to 64 further safe opcodes are admitted without re-entering the helper;
each still performs the sticky-failure probe, the live token comparison and the real
pending/admitted accounting. Unsafe opcodes and their successors, frame/tail transitions,
exception recovery, re-entry, End and cleanup reset or bypass the segment. The source adds no
deadline flag load on the OFF path (it still enters the body directly), but that statement is
about the source shape only: S2 still changes the shared InterpExecMethod code layout, and the
OFF arm regressed in the formal run (see below). The code is compiled only for the Windows
AMD64 Release folded bank.

## Stop point

S2 is **paused and not promoted**. No code from either branch is part of `runeclr/dev`. The public
host option still accepts only 1/100/1000; 10000 is reachable only through the private QCall
used by the experiment. Further optimization is out of scope until a separate decision.

## Evidence boundary

- Windows x64 Release interpreter only. No device, AOT, Linux, Wasm, JIT or R2R instrumentation
  claims, and no unload claims.
- Functional acceptance: matched runs against REF10K with exact counters, Query(4) readback for
  both intervals, host epoch 24/24, lifecycle 156/156 twice, and four focused upstream tests.
- Codegen (disassembly extents): InterpExecMethod 44,491 B (REF10K) / 107,548 B (S1) / 56,410 B
  (S2); helper 619 / 619 / 746 B. The typed failure probe is still an out-of-line call at the 97
  safe-opcode stubs (P2, non-blocking).
- Formal timing: one 90-process paired run (five AB/BA pairs; OFF/10000 label, Epoch/10000
  primary, Epoch/1000 control; three kernels). The portable extracts are in the RuneCLR
  repository under `docs/evidence/epoch-s2-20261115/` and the experiment record under
  `docs/experiments/epoch-large-interval-s2-20261114.md`.
- Median elapsed change (candidate vs reference; TightLoop / CallBoundary / BusinessLoop):
  Epoch/10000 improved by -36.1% / -19.2% / -41.8%; Epoch/1000 by -37.2% / -15.2% / -42.5%;
  OFF/10000 regressed by +32.9% / +7.3% / +16.9%. This is the sole formal run (90 processes,
  all exits 0, no drift); five pairs are exploratory, with no significance,
  minimum-detectable-effect, sub-percent or causal-attribution claim. OFF is an uncharged
  label, not a strict null.
- Bounded disassembly of the OFF arm shows memory reads rising from 2 to 4 and additional
  IP-home reloads/writes, consistent with the shared code-layout change. No small safe fix was
  identified, and the regression cost cannot be apportioned to a specific access from these
  measurements, so the S2 line was stopped rather than tuned further.
- The official integration verifier remains FAIL on the CLR-only build directory (missing
  System.Private.CoreLib.dll); the scoped provenance check that joins each native image to its
  build and the managed set to the frozen cohort is separate and is not an official integrated
  pass.

## References

- RuneCLR experiment record:
  https://github.com/ReneeGA2020/RuneCLR/blob/main/docs/experiments/epoch-large-interval-s2-20261114.md
- RuneCLR portable readings:
  https://github.com/ReneeGA2020/RuneCLR/blob/main/docs/evidence/epoch-s2-20261115/README.md
- Callback-epoch ABI and limits: [runeclr-callback-epoch.md](runeclr-callback-epoch.md)
