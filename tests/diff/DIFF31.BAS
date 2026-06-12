' Differential battery 31: O20 algorithmic idiom replacement must stay
' invisible - closed-form sums, REP STOSW fills and empty-loop counter ends
' have to match the looped originals bit for bit (incl. QUIRK-2.28 end values).
$OPTIMIZE SPEED
DIM a%(100)
OPEN "RESULT.TXT" FOR OUTPUT AS #1
s = 0
FOR i% = 1 TO 1000
  s = s + i%
NEXT i%
PRINT #1, s; i%
t% = 0
FOR j% = 1 TO 200
  t% = t% + j%
NEXT j%
PRINT #1, t%; j%
u% = 100
FOR j% = 1 TO 400
  u% = u% + j%
NEXT j%
PRINT #1, u%; j%
v& = 0
FOR m% = 1 TO 30000
  v& = v& + m%
NEXT m%
PRINT #1, v&; m%
w# = 0.5
FOR n% = -50 TO 75
  w# = w# + n%
NEXT n%
PRINT #1, w#; n%
FOR k% = 5 TO 50
  a%(k%) = 7
NEXT k%
PRINT #1, a%(4); a%(5); a%(50); a%(51); k%
FOR f% = 10 TO 1 STEP -1
  a%(f%) = f%
NEXT f%
PRINT #1, a%(1); a%(10); f%
FOR e% = 1 TO 12345
NEXT e%
PRINT #1, e%
FOR z% = 9 TO 2
NEXT z%
PRINT #1, z%
CLOSE #1
END
