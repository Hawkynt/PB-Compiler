' Differential battery 72: C1 ($CPU 80386) 64-bit bitwise ops (AND/OR/XOR/EQV/IMP).
' Under pb36 $OPTIMIZE SPEED + $CPU 80386 a QUAD bitwise op runs inline as two 32-bit
' halves in EAX instead of the runtime QuadAnd/.. call; bitwise ops can't trap, so the
' inline result (built into scratch, then FILD onto the x87) must be byte-identical to the
' runtime path and to genuine PBC 3.50.
$CPU 80386
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

x&& = 1099511627775
y&& = 76861433640456465
PRINT #1, x&& AND y&&
PRINT #1, x&& OR y&&
PRINT #1, x&& XOR y&&
PRINT #1, x&& EQV y&&
PRINT #1, x&& IMP y&&

p&& = -1
q&& = 0
PRINT #1, p&& AND q&&; p&& OR q&&; p&& XOR q&&; p&& EQV q&&; p&& IMP q&&

r&& = 12297829382473034
s&& = 81985529216486895
PRINT #1, r&& AND s&&; r&& OR s&&; r&& XOR s&&
PRINT #1, r&& EQV s&&; r&& IMP s&&

CLOSE #1
END
