# expand-szdd.ps1 - decompress MS "SZ " (old SZDD / install-media) LZSS files.
# Vintage Microsoft install disks (e.g. BASIC PDS 7.1) store files compressed with
# the old SZDD variant ("SZ\x20\x88\xF0\x27\x33\xD1") that neither 7-Zip nor the
# modern Windows expand.exe handle. Format: 8-byte magic, 4-byte uncompressed
# length (LE) at offset 8, LZSS stream from offset 12 over a 4096-byte ring buffer
# pre-filled with spaces (start position 4096-16); control byte, LSB-first: bit set
# = literal, bit clear = (12-bit window offset, 4-bit length+3) back-reference.
#
#   expand-szdd.ps1 <file.in> <file.out>     # one file
#   expand-szdd.ps1 <dir.in>  <dir.out>      # every file in a dir: SZDD files are
#                                            # expanded (extension's last char $ -> E/B/J/P
#                                            # for .EX$/.LI$/.OB$/.HL$), the rest copied
param([Parameter(Mandatory=$true)][string]$Src, [Parameter(Mandatory=$true)][string]$Dst)
$ErrorActionPreference = 'Stop'

function Expand-One([byte[]]$d) {
  if ($d.Length -lt 12 -or $d[0] -ne 0x53 -or $d[1] -ne 0x5A -or $d[2] -ne 0x20) { return $null }
  $ulen = [int][BitConverter]::ToUInt32($d, 8)
  $win = [byte[]]::new(4096)
  for ($i = 0; $i -lt 4096; $i++) { $win[$i] = 0x20 }
  $wp = 4096 - 16
  $outBuf = [byte[]]::new($ulen)
  $op = 0; $ip = 12
  while ($op -lt $ulen -and $ip -lt $d.Length) {
    $ctrl = $d[$ip]; $ip++
    for ($b = 0; $b -lt 8 -and $op -lt $ulen; $b++) {
      if (($ctrl -band (1 -shl $b)) -ne 0) {
        $c = $d[$ip]; $ip++
        $outBuf[$op] = $c; $op++; $win[$wp] = $c; $wp = ($wp + 1) -band 0xFFF
      } else {
        if ($ip + 1 -ge $d.Length) { break }
        $lo = $d[$ip]; $hi = $d[$ip + 1]; $ip += 2
        $mp = $lo -bor (($hi -band 0xF0) -shl 4)
        $len = ($hi -band 0x0F) + 3
        for ($k = 0; $k -lt $len -and $op -lt $ulen; $k++) {
          $c = $win[$mp]
          $outBuf[$op] = $c; $op++; $win[$wp] = $c; $wp = ($wp + 1) -band 0xFFF; $mp = ($mp + 1) -band 0xFFF
        }
      }
    }
  }
  return $outBuf
}

# map a compressed name's trailing '$' back to the conventional last character
function Map-Name([string]$name) {
  switch -regex ($name) {
    '\.EX\$$' { return ($name -replace '\.EX\$$', '.EXE') }
    '\.LI\$$' { return ($name -replace '\.LI\$$', '.LIB') }
    '\.OB\$$' { return ($name -replace '\.OB\$$', '.OBJ') }
    '\.HL\$$' { return ($name -replace '\.HL\$$', '.HLP') }
    '\$$'     { return ($name -replace '\$$', '_') }
    default   { return $name }
  }
}

if (Test-Path $Src -PathType Container) {
  New-Item -ItemType Directory -Force -Path $Dst | Out-Null
  $n = 0
  foreach ($f in Get-ChildItem -File $Src) {
    $d = [System.IO.File]::ReadAllBytes($f.FullName)
    $exp = Expand-One $d
    if ($null -ne $exp) {
      $name = Map-Name $f.Name
      [System.IO.File]::WriteAllBytes((Join-Path $Dst $name), $exp); $n++
    } else {
      Copy-Item $f.FullName (Join-Path $Dst $f.Name) -Force
    }
  }
  Write-Output ("expanded {0} files into {1}" -f $n, $Dst)
} else {
  $exp = Expand-One ([System.IO.File]::ReadAllBytes($Src))
  if ($null -eq $exp) { throw "$Src is not an old-SZDD 'SZ ' file" }
  [System.IO.File]::WriteAllBytes($Dst, $exp)
  Write-Output ("{0} -> {1} ({2} bytes)" -f (Split-Path $Src -Leaf), (Split-Path $Dst -Leaf), $exp.Length)
}
