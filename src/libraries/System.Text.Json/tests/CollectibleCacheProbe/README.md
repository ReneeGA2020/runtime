# Collectible accessor cache probe

This standalone, DTO-only probe builds independently of the runtime test host. It never
clears, removes or modifies serializer caches. The optional accessor tests invoke internal
factories to check delegate identity; the lifetime tests use serializer APIs and collectible
load contexts. They do not inspect or clean private caches.

Build with an SDK and reference pack supporting **net12.0**:

```powershell
dotnet build Runner/Runner.csproj -c Release
```

An early development SDK may need `-p:NETCoreAppMaximumVersion=12.0` when its bundled
framework table already contains net12.0 and the matching reference pack is installed.
That property alone does not supply missing references or make an older runtime compatible.

Run each mode in a separate process, using an isolated copy of the matching runtime and
the exact library under test. `corerun` resolves its framework libraries from its own core
root; the runner prints the actual CoreLib/STJ locations, target frameworks and STJ SHA256.
Keep the application, Contracts and Fixture build outputs; do not load Fixture into Default.

```powershell
& "$coreRoot/corerun.exe" Runner/bin/Release/net12.0/Runner.dll session `
    Fixture/bin/Release/net12.0/Fixture.dll patched
```

The final argument is `baseline` for the original library or `patched` for the candidate.
`RESULT PASS` means the selected expectations matched, so a baseline PASS is **not** a
successful-unload claim. Inspect `ROUND` and `FOREIGN_RELEASE` as well.

| Mode | Purpose | Candidate expectation |
| --- | --- | --- |
| `no` | Construct and unload without calling the serializer | Two rounds and foreign context unload |
| `session` | Unique resolver and session-owned options/type info | Two rounds and foreign context unload |
| `held` | Deliberately retain options and type info | Stay alive while retained; unload after explicit release |
| `default` | Permanently live `JsonSerializerOptions.Default` | Subject contexts stay alive; foreign remains usable, then unloads |
| `equivalent` | Prewarm Default, copy its options, and assert shared public type-info identity | Same intentional retention control |
| `accessor` | Directly invoke all cached factories; execute delegates and compare identity | 8 stable cache-hit controls; 21 collectible/unproven inputs bypass |
| `identity` | Print actual runtime/library identity without exercising serialization | Matching CoreLib/library TFM required for the net12 candidate |

Each serialization round exercises concurrent serialization/deserialization on four workers,
300 distinct constructed collectible DTO types, an inherited Default-base member whose
ReflectedType is collectible, a Default-defined closed generic spanning two contexts, twelve
nested generic wrappers, arrays, stack addition, immutable collection factories, and small/
large parameterized constructors. Both rounds execute in one process. An independent foreign
context and Default DTO remain usable across the subject unloads. All retained observations
are scalar values or WeakReferences; collection occurs outside the subject helper frame.
Session options, resolver and configured/cached cross-context type-info also have weak-reference
release assertions. Held controls retain that cached metadata, and assert that **both** subject
contexts remain alive until the user reference is released. Distinct nondefault DTO values,
nonempty collections and constructor inputs are checked by JSON value equality and exact
deserialized type identity, rather than counting successful calls alone.

The factory cases include each of the four small-constructor delegate argument slots while
the constructor/key remain stable, covariant constructor results, collectible receivers,
and forwarding custom Type/PropertyInfo instances that report `IsCollectible == false` but
hold an additional collectible object. An implementation's reported flag alone cannot prove
that an arbitrary user-defined reflection wrapper is safe to keep globally.

Append `required` to run the author context through this fork's optional interpreter-required
loader. Its policy is checked as 1 and collectible; framework/foreign remain ordinary. This
checks loader configuration and functional behavior, not per-method execution attestation.
It is unavailable on a standard runtime.

The original accessor cache can retain collectible inputs or delegate shapes even after all
session references are released. The patch bypasses that **global accessor cache** on modern
.NET for collectible or unproven reflection inputs, including the actual returned delegate
type. Stable noncollectible accessors retain the existing cache and expiration behavior.
The .NET Framework implementation is unchanged because that target lacks these reflection APIs.

This does not change options/type-info caching. A live Default/shared options instance,
resolver, converter, JsonTypeInfo, delegate, object or exception can legitimately retain a
collectible type. Use a session-owned resolver/options graph that cannot share a permanently
live equivalent caching context, and release the whole graph before unload. Creating a new
options instance by itself does not establish that ownership.

GC polling is bounded evidence for these inputs, not a fixed-cycle GC guarantee or complete
serializer API/performance certification. No throughput benchmark is part of this probe.
