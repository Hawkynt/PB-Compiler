' Differential battery 70: C1 ($CPU 80386) 32-bit SHIFT/ROTATE statement.
' Under pb36 $OPTIMIZE SPEED + $CPU 80386 a LONG SHIFT/ROTATE by a compile-time-constant
' count in 1..31 collapses the per-bit CX loop to a single 386 dword shift; the result
' must be byte-identical to the per-bit loop and to genuine PBC 3.50 (which accepts
' $CPU 80386 and emits its own correct code). Variable and zero counts use the loop.
$CPU 80386
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

x& = 3
SHIFT LEFT x&, 4
PRINT #1, "shl"; x&
SHIFT RIGHT x&, 2
PRINT #1, "shr"; x&

y& = 1
SHIFT LEFT y&, 20
PRINT #1, "shl20"; y&
ROTATE LEFT y&, 4
PRINT #1, "rol"; y&
ROTATE RIGHT y&, 8
PRINT #1, "ror"; y&

' top-bit set: the statement's SHIFT RIGHT is logical (zero fill)
z& = &H80000000
SHIFT RIGHT z&, 4
PRINT #1, "shrhi"; z&

' rotate across the word boundary
w& = &H12345678
ROTATE LEFT w&, 8
PRINT #1, "rolw"; w&
ROTATE RIGHT w&, 16
PRINT #1, "rorw"; w&

' variable count -> per-bit loop fallback (no 386 collapse), still correct
n% = 5
v& = 1
SHIFT LEFT v&, n%
PRINT #1, "var"; v&

' zero count -> no change
u& = 99
SHIFT LEFT u&, 0
PRINT #1, "zero"; u&

CLOSE #1
END
