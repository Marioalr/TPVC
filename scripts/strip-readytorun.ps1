<#
.SYNOPSIS
    Quita el código precompilado (ReadyToRun) de los ensamblados de una carpeta y los deja como IL puro.

.DESCRIPTION
    Solo para la CI de Linux (paso 8.2 del plan de la Fase 8). El proyecto de pruebas es net10.0-windows con WPF
    y su testhost necesita Microsoft.WindowsDesktop.App, que se copia del zip oficial de Windows. Esos ensamblados
    vienen precompilados para Windows x64 y el runtime de Linux los rechaza ("An attempt was made to load a
    program with an incorrect format"). Conservan su IL completo: basta con marcar el ensamblado como "solo IL"
    (COR flags: se quita IL_LIBRARY y se pone ILONLY) y borrar el puntero al encabezado de ReadyToRun para que
    el runtime los cargue y los compile al vuelo. Los ensamblados nativos o que ya son IL puro no se tocan.
    Nunca se usa con lo que se publica: TPVC.exe corre con el runtime de Windows sin cambios.

.EXAMPLE
    ./scripts/strip-readytorun.ps1 -Path "$env:DOTNET_ROOT/shared/Microsoft.WindowsDesktop.App/10.0.12"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'
$ComFlagsIlOnly = 0x1
$ComFlagsIlLibrary = 0x4

$patched = 0
$skipped = 0
foreach ($file in Get-ChildItem -Path $Path -Filter *.dll -File) {
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    if ($bytes.Length -lt 0x40 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { $skipped++; continue }

    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    if ([BitConverter]::ToUInt32($bytes, $pe) -ne 0x00004550) { $skipped++; continue }

    $sectionCount = [BitConverter]::ToUInt16($bytes, $pe + 6)
    $optionalHeaderSize = [BitConverter]::ToUInt16($bytes, $pe + 20)
    $magic = [BitConverter]::ToUInt16($bytes, $pe + 24)
    $dataDirectories = if ($magic -eq 0x20B) { $pe + 24 + 112 } else { $pe + 24 + 96 }
    $cliRva = [BitConverter]::ToUInt32($bytes, $dataDirectories + 14 * 8)
    if ($cliRva -eq 0) { $skipped++; continue } # nativo

    $cliOffset = $null
    $sections = $pe + 24 + $optionalHeaderSize
    for ($i = 0; $i -lt $sectionCount; $i++) {
        $s = $sections + 40 * $i
        $virtualSize = [BitConverter]::ToUInt32($bytes, $s + 8)
        $virtualAddress = [BitConverter]::ToUInt32($bytes, $s + 12)
        $rawSize = [BitConverter]::ToUInt32($bytes, $s + 16)
        $rawPointer = [BitConverter]::ToUInt32($bytes, $s + 20)
        $size = [Math]::Max($virtualSize, $rawSize)
        if ($cliRva -ge $virtualAddress -and $cliRva -lt $virtualAddress + $size) { $cliOffset = $cliRva - $virtualAddress + $rawPointer }
    }
    if ($null -eq $cliOffset) { $skipped++; continue }

    # Encabezado CLI: Flags en +16, ManagedNativeHeader (RVA y tamaño) en +64
    $flags = [BitConverter]::ToUInt32($bytes, $cliOffset + 16)
    $nativeHeaderRva = [BitConverter]::ToUInt32($bytes, $cliOffset + 64)
    if ($nativeHeaderRva -eq 0) { $skipped++; continue } # ya es IL puro

    $newFlags = ($flags -band (-bnot $ComFlagsIlLibrary)) -bor $ComFlagsIlOnly
    [BitConverter]::GetBytes([uint32]$newFlags).CopyTo($bytes, $cliOffset + 16)
    [BitConverter]::GetBytes([uint64]0).CopyTo($bytes, $cliOffset + 64)

    # Un ensamblado "solo IL" solo puede tener estos directorios (PEDecoder::CheckILOnly del runtime): recursos,
    # firma, depuración y el encabezado CLI. Los demás (excepciones y reubicaciones del código nativo, entre otros)
    # eran de ReadyToRun y el IL no los usa.
    $allowed = @(2, 4, 6, 14)
    $directoryCount = [BitConverter]::ToUInt32($bytes, $dataDirectories - 4)
    for ($entry = 0; $entry -lt $directoryCount; $entry++) {
        if ($allowed -notcontains $entry) { [BitConverter]::GetBytes([uint64]0).CopyTo($bytes, $dataDirectories + 8 * $entry) }
    }
    [System.IO.File]::WriteAllBytes($file.FullName, $bytes)
    $patched++
}

Write-Host "Ensamblados sin ReadyToRun: $patched; sin cambios (nativos o ya IL): $skipped"
if ($patched -eq 0) { throw "No se encontró ningún ensamblado ReadyToRun en $Path" }
