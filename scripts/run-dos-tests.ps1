# =============================================================================
# run-dos-tests.ps1 - Windows-local twin of run-dos-tests.sh: compile the PB
# battery with our compiler on the host, run every EXE under DOSBox, verify.
# =============================================================================
# Verification per tests/<NAME>.BAS:
#   - tests/<NAME>.expected present -> compare redirected stdout (trailing
#     whitespace stripped per line; PB prints numerics with a trailing space).
#   - otherwise the program is a TESTLIB battery appending to UNITTEST.LOG.
# dosbox-staging blocks autoexec `exit` for fast programs (anti-vanish UX), so
# each run writes a DONE.TXT sentinel and the harness kills the emulator.
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$dosbox = $env:DOSBOX_EXE
if (-not $dosbox) {
  $dosbox = Get-ChildItem -Path "tools/dosbox" -Filter "dosbox.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $dosbox) { Write-Error "no DOSBox found (set DOSBOX_EXE)"; exit 1 }

Write-Host "building compiler ..."
dotnet build pbc -c Release -v q --nologo | Out-Null

if (Test-Path build) { Remove-Item build -Recurse -Force }
New-Item -ItemType Directory build | Out-Null
Copy-Item tests/*.BI build/ -ErrorAction SilentlyContinue

$fail = $false
$i = 0
foreach ($t in Get-ChildItem tests/*.BAS) {
  $i++
  $name = $t.BaseName
  Copy-Item $t.FullName "build/T$i.BAS"

  # auto-generate the TESTLIB driver from SUB Test_* names unless one is wired
  $src = Get-Content "build/T$i.BAS" -Raw
  if ($src -match '(?im)^\s*SUB\s+Test_' -and $src -notmatch '(?i)Test_BeginSuite') {
    $subs = [regex]::Matches($src, '(?im)^\s*SUB\s+(Test_[A-Za-z0-9_]+)') | ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -notmatch '(?i)^Test_(Setup|Teardown)$' }
    $driver = "`r`n' === auto-generated test driver ===`r`n"
    if ($src -match '(?im)^\s*SUB\s+Test_Setup\s*$') { $driver += "CALL Test_Setup`r`n" }
    $driver += "CALL Test_BeginSuite(`"$name`")`r`n"
    foreach ($fn in $subs) { $driver += "CALL $fn`r`n" }
    $driver += "CALL Test_EndSuite(`"$name`")`r`n"
    if ($src -match '(?im)^\s*SUB\s+Test_Teardown\s*$') { $driver += "CALL Test_Teardown`r`n" }
    $driver += "END`r`n"
    Add-Content "build/T$i.BAS" $driver
  }

  & dotnet run --project pbc -c Release --no-build -v q -- "build/T$i.BAS" -O "build/T$i.EXE" 2>&1 | Out-File "build/T$i.pbcout"
  if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL  $name (compile)"
    Get-Content "build/T$i.pbcout" | ForEach-Object { Write-Host "      $_" }
    $fail = $true
    continue
  }

  $buildDir = (Resolve-Path build).Path
  # tests/<NAME>.IN, when present, is redirected into the program's stdin
  $run = "T$i.EXE > T$i.OUT"
  if (Test-Path "tests/$name.IN") {
    Copy-Item "tests/$name.IN" "build/T$i.IN"
    $run = "T$i.EXE < T$i.IN > T$i.OUT"
  }
  @"
[sdl]
[dosbox]
ems=true
[autoexec]
mount c "$buildDir"
c:
$run
echo ok > DONE.TXT
exit
"@ | Set-Content "build/dosbox-T$i.conf"

  Remove-Item "build/DONE.TXT" -ErrorAction SilentlyContinue
  $proc = Start-Process -FilePath $dosbox -ArgumentList "-conf", "build/dosbox-T$i.conf" -PassThru
  $deadline = (Get-Date).AddSeconds(120)
  while (-not (Test-Path "build/DONE.TXT") -and -not $proc.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
  $finished = (Test-Path "build/DONE.TXT") -or $proc.HasExited
  if (-not $proc.HasExited) { Start-Sleep -Milliseconds 300; Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
  if (-not $finished) { Write-Host "FAIL  $name (hang)"; $fail = $true; continue }

  $expected = "tests/$name.expected"
  if (Test-Path $expected) {
    $norm = { param($file) (Get-Content $file -ErrorAction SilentlyContinue | ForEach-Object { $_.TrimEnd() }) -join "`n" }
    $actualText = & $norm "build/T$i.OUT"
    $expectedText = & $norm $expected
    if ($actualText -eq $expectedText) {
      Write-Host "PASS  $name"
    } else {
      Write-Host "FAIL  $name (output mismatch)"
      Write-Host "--- expected ---`n$expectedText`n--- actual ---`n$actualText"
      $fail = $true
    }
  } else {
    Write-Host "RAN   $name (battery - results in UNITTEST.LOG)"
  }
}

# evaluate the shared TESTLIB battery log, if any suite wrote one
$log = "build/UNITTEST.LOG"
if (Test-Path $log) {
  Write-Host "`n=================== PB test battery ==================="
  $lines = Get-Content $log
  $total = ($lines | Where-Object { $_ -match '^\s+\[(PASS|FAIL)\]' }).Count
  $failed = ($lines | Where-Object { $_ -match '^\s+\[FAIL\]' })
  $failed | ForEach-Object { Write-Host "  FAIL $_" }
  Write-Host "Total: $total  Failed: $($failed.Count)"
  $started = ($lines | Where-Object { $_ -match '^\[SUITE\]' }).Count
  $finishedSuites = ($lines | Where-Object { $_ -match '^\[RESULT\]' }).Count
  if ($started -ne $finishedSuites) { Write-Host "ERROR: suite crashed/hung (started=$started finished=$finishedSuites)"; $fail = $true }
  if ($failed.Count -gt 0) { $fail = $true }
}

if ($fail) { exit 1 } else { Write-Host "all good"; exit 0 }
