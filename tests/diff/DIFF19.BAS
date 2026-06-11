' Differential battery 19: $ERROR ... ON semantics - the wrap/overflow/bounds
' cases of DIFF18 now raise trappable errors 6/9 through ON ERROR (real PBC
' requires the $ERROR metastatements before any executable statement).
$ERROR NUMERIC ON
$ERROR OVERFLOW ON
$ERROR BOUNDS ON
$ERROR STACK ON
DECLARE SUB Recurse(d%)
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' ---- BYTE counter at type max raises error 6 instead of wrapping ------------
ON ERROR GOTO Trap1
e% = 0
c% = 0
FOR b? = 250 TO 255
  INCR c%
NEXT b?
AfterNumeric:
PRINT #1, e%; c%

' ---- signed INTEGER overflow raises error 6 ---------------------------------
ON ERROR GOTO Trap2
e% = 0
a% = 30000
o% = a% + a%
AfterOverflow:
PRINT #1, e%

' ---- out-of-range index raises error 9 --------------------------------------
ON ERROR GOTO Trap3
e% = 0
DIM ar%(1 TO 5)
i% = 7
ar%(i%) = 1
AfterBounds:
PRINT #1, e%

' in-range accesses still work with checking on
ar%(1) = 11
ar%(5) = 55
PRINT #1, ar%(1); ar%(5)

' ---- runaway recursion raises error 7 (stack probe) -------------------------
ON ERROR GOTO Trap4
e% = 0
Recurse 1
AfterStack:
PRINT #1, e%

CLOSE #1
END

SUB Recurse(d%)
  Recurse d% + 1
END SUB

Trap4:
e% = ERR
RESUME AfterStack

Trap1:
e% = ERR
RESUME AfterNumeric

Trap2:
e% = ERR
RESUME AfterOverflow

Trap3:
e% = ERR
RESUME AfterBounds
