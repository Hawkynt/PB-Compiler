# Generate a comparable optimized/reference DOS EXE for every specified x86 tier.
[CmdletBinding()]
param(
  [ValidateSet('8086', '80286', '80386')]
  [string[]]$Cpu = @('8086', '80286', '80386')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$source = [IO.File]::ReadAllText((Join-Path $root 'tests/benchmarks/SCREEN12.BAS'))
$output = Join-Path $root 'build/vga-hardware'
[IO.Directory]::CreateDirectory($output) | Out-Null

foreach ($tier in $Cpu) {
  $prefix = switch ($tier) { '8086' { '86' } '80286' { '286' } '80386' { '386' } }
  $temporary = Join-Path $output "V$prefix.BAS"
  [IO.File]::WriteAllText($temporary, $source.Replace('$CPU 8086', '$CPU ' + $tier))
  foreach ($variant in @('OPT', 'REF')) {
    $exe = Join-Path $output "V$prefix$variant.EXE"
    $flags = if ($variant -eq 'OPT') { @('--optimize') } else { @('--no-optimize') }
    & dotnet run --project (Join-Path $root 'pbc') --configuration Release --no-build -- --dialect pb36 @flags $temporary -O $exe
    if ($LASTEXITCODE -ne 0) { throw "Compiler failed for $tier / $variant" }
    Write-Output "$tier $variant : $exe"
  }
}
