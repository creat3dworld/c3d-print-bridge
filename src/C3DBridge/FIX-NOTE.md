# Fix note — bridge-auto-start build failure (BambuAdapter.cs)

## What was broken

`dotnet build -c Release` (shop PC, SDK 8.0.427) failed with 72 errors, all in
`Printers/BambuAdapter.cs`, starting at line 311: CS1519 (invalid token '{'
in class member declaration), cascading into CS1001/CS1513/CS0116/CS0106/
CS1022. No exe was produced; nothing was installed; the live bridge was
untouched.

Root cause: a method body with its declaration line missing. Line 310 closed
the preceding method (`}`), and line 311 opened a bare `{`. The orphaned
body is the MQTT command publisher: it checks identity, opens an MQTT
client to `_cfg.Ip`/`_mqttPort` with credentials `bblp`/`_cfg.AccessCode`,
publishes `JsonSerializer.Serialize(command)` to
`device/{_cfg.Serial}/request`, and returns `(bool, string)` tuples. With
no declaration, the parser treated the body and every following method as
namespace-level members, producing the whole cascade.

## The fix (minimal, one line)

File: `Printers/BambuAdapter.cs`, inserted before the orphaned `{`
(now line 312):

```csharp
private async Task<(bool Ok, string Reason)> PublishCommandAsync(object command, CancellationToken ct)
```

The signature is NOT guessed. It is taken verbatim from the pre-build
original at `build/bridge-farm-controls-work/Printers/BambuAdapter.cs`
line 238, and the orphaned body is byte-identical to that original
method's body. It matches all five call sites in the same file
(`StartPrintAsync`, `PausePrintAsync`, `CancelPrintAsync`,
`SetChamberLedAsync`, `HomeAsync`), each of which does
`return PublishCommandAsync(command, ct);` from a method returning
`Task<(bool Ok, string Reason)>`, passing an anonymous-type command
(`object`) and a `CancellationToken`. No callers exist outside this
class, so `private` (as in the original) is correct. No behavior was
changed: the body is untouched.

## What was verified

- Recursive diff of the entire fix work dir vs the failed dir: the ONLY
  difference is the two added lines (blank + declaration) in
  `BambuAdapter.cs`. No other file touched; no scope or behavior change.
- Brace/paren/bracket balance of the fixed file (strings and comments
  stripped before counting): braces 76/76, parens 286/286, brackets
  16/16, final nesting depth 0.
- Structural check: every method declaration sits at depth 1 (inside
  the `BambuAdapter` class), including the restored `PublishCommandAsync`
  at line 312. The class closing brace before `ImplicitFtps` was already
  present — no missing closing brace at file end.
- No other structural break exists in the file: the whole 72-error
  cascade traces to this single missing declaration line.
- Compile check: NO dotnet SDK exists in this environment, so no local
  build was run and none is claimed. The parent rebuilds on the shop PC
  via Action1 regardless; a green shop-PC build is still the gate
  before any install.

## For the parent's rebuild

Source zip recipe: contents of `bridge-auto-start-fix-work/` at root,
`bin/` and `obj/` excluded. Install rules unchanged from the
auto-start run sequence: verify size + MD5 before building, back up
`C:\C3D_Bridge\app\` first, copy Release outputs only (never overwrite
`bridge-config.json` / `bridge-key.txt`), restart ONLY via the
scheduled task "C3D Print Bridge". The live dashboard (2026.1029.1859-
farm-auto) and live bridge (farm-controls build) are untouched by this
fix work.
