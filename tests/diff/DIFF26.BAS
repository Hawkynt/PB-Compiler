' Differential battery 26: pb36 optimizer passes must stay invisible to the oracle.
' Exercises O2 (dead code), O7 (small-trip FOR unroll + QUIRK-2.28 counter end),
' O9 (literal concat fold), O10 (DEF SEG coalescing), O11 (literal overlap pool).
$OPTIMIZE SPEED
DIM i AS INTEGER, total AS INTEGER
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' O7: constant-trip unrollable loop - sum and the counter's final value both observable
total = 0
FOR i = 1 TO 3
    total = total + i
NEXT i
PRINT #1, total
PRINT #1, i

' O7: zero-trip loop assigns the start value
FOR i = 5 TO 1
    total = total + 1
NEXT i
PRINT #1, i

' O7: EXIT FOR is not unrolled away
total = 0
FOR i = 1 TO 100
    IF i > 4 THEN EXIT FOR
    total = total + i
NEXT i
PRINT #1, total

' O2: dead code after GOTO never runs
GOTO past
PRINT #1, "DEAD"
past:
PRINT #1, "LIVE"

' O9 / O11: folded and overlapping literals print identically
PRINT #1, "Hello, " & "World!"
PRINT #1, "World!"
PRINT #1, "lo, W"

CLOSE #1
END
