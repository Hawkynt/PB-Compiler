' Differential battery 66: loop-invariant code motion (LICM). Under pb36 $OPTIMIZE SPEED,
' a pure integer/modular subexpression in a FOR loop body whose operands are all
' loop-invariant is hoisted to the preheader and reloaded each iteration; the observable
' values must be byte-identical to pb35, including zero-trip loops and the case where an
' input is written in the loop (then it must NOT hoist).
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1
DIM a%(1 TO 20)
k% = 7
m% = 11
FOR i% = 1 TO 20
  a%(i%) = k% * m% + i%
NEXT i%
t& = 0
FOR i% = 1 TO 20
  t& = t& + a%(i%)
NEXT i%
PRINT #1, "t"; t&; a%(1); a%(20)
' zero-trip
FOR i% = 9 TO 2
  c% = k% * m% - i%
  PRINT #1, c%
NEXT i%
PRINT #1, "empty-done"
' input m% written in body: must NOT hoist (value changes each iteration)
FOR i% = 1 TO 5
  PRINT #1, k% * m% + i%
  m% = m% + 2
NEXT i%
PRINT #1, "m"; m%
CLOSE #1
END
