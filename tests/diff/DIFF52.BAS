' Differential battery 52: O5 loop accumulator in a register (DI). Under
' $OPTIMIZE SPEED a hot 2-byte INTEGER accumulator in an SI/DI-clean FOR loop
' lives in DI alongside the SI counter - load before the loop, flush on exit.
' Every result (sum, pre-loop value, INCR, multiply, descending, zero-trip,
' read-inside, signed wrap) must equal the memory-cell version, byte-identical.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

s% = 0
FOR i% = 1 TO 10
  s% = s% + i%
NEXT i%
PRINT #1, s%; i%

t% = 100
FOR j% = 1 TO 5
  t% = t% + j%
NEXT j%
PRINT #1, t%

c% = 0
FOR k% = 1 TO 7
  INCR c%
NEXT k%
PRINT #1, c%

p% = 0
FOR m% = 5 TO 1 STEP -1
  p% = p% + m% * 2
NEXT m%
PRINT #1, p%

z% = 42
FOR n% = 5 TO 1
  z% = z% + 1
NEXT n%
PRINT #1, z%

q% = 1
FOR a% = 1 TO 4
  q% = q% + q%
NEXT a%
PRINT #1, q%

w% = 30000
FOR b% = 1 TO 3
  w% = w% + 10000
NEXT b%
PRINT #1, w%

CLOSE #1
END
