param(
    [Parameter(Mandatory = $true)][string]$InputDll,
    [Parameter(Mandatory = $true)][string]$OutputDll,
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)
$ErrorActionPreference = 'Stop'
# Narrow, reproducible IL repair of the existing vendored library. No game DLL is written.
if ((Get-FileHash -LiteralPath $InputDll -Algorithm SHA256).Hash -ne
    '166956302A294E224474B26F4C7D58409084AD3F48BD0AF1FEB7551F229C8F60') {
    throw 'Unexpected ServerSync input. Review a different library version separately.'
}
if ([IO.Path]::GetFullPath($InputDll) -eq [IO.Path]::GetFullPath($OutputDll)) {
    throw 'Use a separate output path.'
}
Add-Type -Path (Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    (Join-Path $GamePath 'valheim_Data\Managed\assembly_valheim.dll'))
$library = [Mono.Cecil.AssemblyDefinition]::ReadAssembly([IO.Path]::GetFullPath($InputDll))
try {
    $field = ($game.MainModule.Types | Where-Object Name -eq 'ZRoutedRpc').Fields |
        Where-Object Name -eq 'Everybody'
    if (!$field.IsLiteral -or $field.FieldType.FullName -ne 'System.Int64' -or
        $field.Constant -ne 0) { throw 'Unexpected target Everybody contract.' }
    $changes = [Collections.Generic.List[object]]::new()
    function Find-Reads($types) {
        foreach ($type in $types) {
            foreach ($method in $type.Methods) {
                if (!$method.HasBody) { continue }
                foreach ($instruction in $method.Body.Instructions) {
                    if ($instruction.Operand -is [Mono.Cecil.FieldReference] -and
                        $instruction.Operand.FullName -eq 'System.Int64 ZRoutedRpc::Everybody') {
                        if ($instruction.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ldsfld) {
                            throw "Unexpected field operation: $instruction"
                        }
                        $changes.Add($instruction)
                        Write-Output "Repair: $($method.FullName)"
                    }
                }
            }
            Find-Reads $type.NestedTypes
        }
    }
    Find-Reads $library.MainModule.Types
    if ($changes.Count -ne 3) { throw "Expected 3 reads; found $($changes.Count)." }
    foreach ($instruction in $changes) {
        # Mutate in place so branch and exception-handler targets remain intact.
        $instruction.OpCode = [Mono.Cecil.Cil.OpCodes]::Ldc_I8
        $instruction.Operand = [long]$field.Constant
    }
    $library.Write([IO.Path]::GetFullPath($OutputDll))
} finally {
    $library.Dispose()
    $game.Dispose()
}
Get-FileHash -LiteralPath $OutputDll -Algorithm SHA256
