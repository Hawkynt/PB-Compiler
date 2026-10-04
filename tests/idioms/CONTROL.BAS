' control flow: DEF FN, ON GOSUB/GOTO, DO/LOOP with ITERATE and EXIT, WHILE, SELECT CASE over ranges, FOR with fractional and negative steps
DEF FNsq(x) = x * x
DEF FNcat$(a$, b$) = a$ + "-" + b$
PRINT FNsq(7); FNcat$("a", "b")
FOR i% = 1 TO 3
  ON i% GOSUB One, Two, Three
NEXT
k% = 2
ON k% GOTO L1, L2
L1:
PRINT "L1"
L2:
PRINT "L2"
n% = 0
DO
  INCR n%
  IF n% = 3 THEN ITERATE DO
  IF n% > 5 THEN EXIT DO
  PRINT n%;
LOOP
PRINT
WHILE n% > 0: DECR n%, 2: WEND
PRINT n%
SELECT CASE 17
  CASE 1 TO 9: PRINT "small"
  CASE > 15, 12: PRINT "big"
  CASE ELSE: PRINT "other"
END SELECT
SELECT CASE "pear"
  CASE "apple" TO "orange": PRINT "a-o"
  CASE ELSE: PRINT "late"
END SELECT
x = 5: IF x > 3 AND x < 9 THEN PRINT "in" ELSE PRINT "out"
PRINT MAX(3, 9, 4); MIN(5, -2)
FOR r! = 1 TO 2 STEP .5: PRINT r!;: NEXT: PRINT
FOR q% = 10 TO 1 STEP -3: PRINT q%;: NEXT: PRINT
END
One:
PRINT "one": RETURN
Two:
PRINT "two": RETURN
Three:
PRINT "three": RETURN
