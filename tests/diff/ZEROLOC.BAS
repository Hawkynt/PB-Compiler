DECLARE SUB Fresh(BYVAL n%)
DECLARE SUB Touch(v%)
TYPE P
  x AS INTEGER
  y AS LONG
END TYPE
OPEN "RESULT.TXT" FOR OUTPUT AS #1
Fresh 1
Fresh 2
Fresh 3
CLOSE #1
SYSTEM

SUB Fresh(BYVAL n%)
  DIM a%(1 TO 5)
  DIM r AS P
  a%(n%) = n% * 10
  s% = 0
  FOR i% = 1 TO 5
    s% = s% + a%(i%)
  NEXT i%
  IF n% = 2 THEN c% = 7
  r.x = n%
  PRINT #1, u%; c%; s%; r.x; r.y; d#; t$; "|"
  u% = 99
  Touch w%
  PRINT #1, w%
END SUB

SUB Touch(v%)
  PRINT #1, "in"; v%
  v% = v% + 1
END SUB
