# ServerSync: Valheim 1.0.7 compatibility repair

The existing vendored ServerSync is retained. Three `ldsfld ZRoutedRpc.Everybody`
instructions are replaced with `ldc.i8 0`, matching the original 1.0.7 game's
`public const long Everybody = 0`. No other library methods, protocol, version,
configuration locks, administrator checks or game DLLs are changed.

Affected methods: `ConfigSync.sendZPackage(long, ZPackage)`, the captured
`AddConfigEntry` change handler, and the captured `AddCustomValue` change handler.
The latter executes during YNW initialization, before network synchronization.
Recompiling YNW alone cannot fix instructions inside the precompiled library.

Original DLL (commit `7a291283b9e633fe7b8efb73fd96077a12c4b7b2`):
`166956302A294E224474B26F4C7D58409084AD3F48BD0AF1FEB7551F229C8F60`.

Repaired DLL:
`EB93074622090E0B27FFACE68FEB2A8B746BF963398532A3A5DDF74A8CA53F8D`.

Reproduce from the repository root without changing game files:

```powershell
git archive --format=zip --output=obj/serversync-baseline.zip 7a29128 Libs/ServerSync.dll
Expand-Archive -LiteralPath obj/serversync-baseline.zip -DestinationPath obj/serversync-baseline -Force
./Verification/Update-ServerSyncForValheim1.ps1 -InputDll obj/serversync-baseline/Libs/ServerSync.dll -OutputDll obj/ServerSync.Valheim1.dll
```

The repair requires the exact input hash, the target game's Int64 literal with
value zero, and exactly three field reads. It mutates instructions in place to
preserve branch/exception targets and writes a separate output. The tool uses
the installed BepInEx Mono.Cecil; it does not publicize any assembly.

`Verification/Verify-GameReferences.ps1` checks the **merged plugin** against
original client/server references. The .NET verifier resolves ServerSync's
Harmony targets; the headless client startup check also exercised actual Mono
initialization. Remote synchronization, admin changes and reconnects still need
a multiplayer run. This is a local compatibility repair, not an upstream release.
