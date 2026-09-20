param(
    [string]$PluginDll = "$PSScriptRoot\..\bin\Debug\YouAreNotWorthy.dll",
    [string]$ManagedPath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed',
    [string]$BepInExPath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx'
)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $BepInExPath 'core\Mono.Cecil.dll')
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($ManagedPath)
$resolver.AddSearchDirectory((Join-Path $BepInExPath 'core'))
$parameters = [Mono.Cecil.ReaderParameters]::new()
$parameters.AssemblyResolver = $resolver
$plugin = [Mono.Cecil.AssemblyDefinition]::ReadAssembly([IO.Path]::GetFullPath($PluginDll), $parameters)
try {
    $gameNames = @('assembly_valheim', 'assembly_utils', 'assembly_guiutils')
    $count = 0
    foreach ($reference in @($plugin.MainModule.GetTypeReferences()) + @($plugin.MainModule.GetMemberReferences())) {
        $type = if ($reference -is [Mono.Cecil.TypeReference]) { $reference } else { $reference.DeclaringType }
        if ($type.Scope.Name -notin $gameNames) { continue }
        $definition = $reference.Resolve()
        if ($null -eq $definition) { throw "Unresolved game reference: $reference" }
        $expectedPath = [IO.Path]::GetFullPath((Join-Path $ManagedPath ($definition.Module.Assembly.Name.Name + '.dll')))
        if ([IO.Path]::GetFullPath($definition.Module.FileName) -ne $expectedPath) {
            throw "Unexpected resolved game DLL: $($definition.Module.FileName)"
        }
        $count++
    }
    function Test-Instructions($types) {
        foreach ($type in $types) {
            foreach ($method in $type.Methods) {
                if (!$method.HasBody) { continue }
                foreach ($instruction in $method.Body.Instructions) {
                    $operand = $instruction.Operand
                    if ($operand -is [Mono.Cecil.FieldReference] -and $operand.DeclaringType.Scope.Name -in $gameNames) {
                        $field = $operand.Resolve()
                        if ($field.IsLiteral -and $instruction.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ldtoken) {
                            throw "Invalid literal-field operation in $($method.FullName): $instruction"
                        }
                    }
                }
            }
            Test-Instructions $type.NestedTypes
        }
    }
    Test-Instructions $plugin.MainModule.Types
    Write-Output "PASS: $count game type/member references resolve to original DLLs; no literal-field load/store."
    foreach ($name in $gameNames) { Get-FileHash -LiteralPath (Join-Path $ManagedPath ($name + '.dll')) -Algorithm SHA256 }
} finally {
    $plugin.Dispose()
    $resolver.Dispose()
}
