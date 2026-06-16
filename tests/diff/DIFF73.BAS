' Differential battery 73: C1 ($CPU 80386) 64-bit SHIFT LEFT/RIGHT by a constant.
' Under pb36 $OPTIMIZE SPEED + $CPU 80386 a QUAD SHIFT (not ROTATE) by a compile-time
' constant count in 1..31 collapses the per-bit loop to a double-precision SHLD/SHRD across
' the two dword halves (EAX/EDX only). The result must be byte-identical to the loop and to
' genuine PBC 3.50. Counts >= 32 and variable counts stay on the loop.
$CPU 80386
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

s&& = 3
SHIFT LEFT s&&, 4
PRINT #1, "shl4"; s&&
SHIFT RIGHT s&&, 2
PRINT #1, "shr2"; s&&

t&& = 1
SHIFT LEFT t&&, 31
PRINT #1, "shl31"; t&&

u&& = 1
SHIFT LEFT u&&, 20
PRINT #1, "shl20"; u&&

' shift whose bits cross the low/high dword boundary (count still < 32)
v&& = 4294967296
SHIFT RIGHT v&&, 4
PRINT #1, "shrx"; v&&

w&& = 255
SHIFT LEFT w&&, 28
PRINT #1, "shl28"; w&&

' top-bit-set logical right shift
z&& = 9223372036854775807
SHIFT RIGHT z&&, 8
PRINT #1, "shrhi"; z&&

' variable count -> per-bit loop fallback
n% = 5
x&& = 1
SHIFT LEFT x&&, n%
PRINT #1, "var"; x&&

' count >= 32 -> per-bit loop fallback (crosses the dword boundary entirely)
y&& = 1
SHIFT LEFT y&&, 40
PRINT #1, "shl40"; y&&

CLOSE #1
END
