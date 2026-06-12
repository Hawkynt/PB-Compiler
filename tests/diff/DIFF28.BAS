' float-counter shapes: pb36 silently re-types provably-integral SINGLEs -
' every observable (loop counts, end values, prints, subscripts) must stay
' byte-identical to the x87 original
DECLARE SUB Touch(x)
OPEN "RESULT.TXT" FOR OUTPUT AS #1
DIM a%(20)
FOR i = 1 TO 10
  a%(i) = i * 2
NEXT i
PRINT #1, i; a%(3); a%(10)
FOR j = 10 TO 1 STEP -2
  PRINT #1, j;
NEXT j
PRINT #1, j
FOR k = 1 TO 100000 STEP 9999
PRINT #1, k;
NEXT k
PRINT #1, k
FOR n = 1 TO 5
  IF n > 2 THEN PRINT #1, n \ 2;
  SELECT CASE n
    CASE 4
      PRINT #1, "four";
  END SELECT
NEXT n
PRINT #1, n
d = 0
FOR m = 3 TO 1
  d = 1
NEXT m
PRINT #1, m; d
' blocked twins: division and call keep these on the x87 original
FOR p = 1 TO 4
  PRINT #1, p / 2;
NEXT p
PRINT #1, p
q = 0
FOR r = 1 TO 3
  Touch r
  q = 2
NEXT r
PRINT #1, r; q
CLOSE #1
END
SUB Touch(x)
  x = x + 0
END SUB
