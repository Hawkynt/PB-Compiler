' tail self-calls: pb36 reuses the frame (constant stack); observable totals,
' parameter end states and blocked twins must match the genuine call chain
DECLARE SUB Sum(BYVAL n%)
DECLARE SUB Twice(n&)
DECLARE SUB Gauss(BYVAL n&, BYVAL acc&)
OPEN "RESULT.TXT" FOR OUTPUT AS #1
total% = 0
Sum 25
PRINT #1, total%
m& = 3
Twice m&
PRINT #1, m&
Gauss 50, 0
CLOSE #1
END
SUB Sum(BYVAL n%)
  SHARED total%
  total% = total% + n%
  IF n% > 0 THEN Sum n% - 1
END SUB
SUB Twice(n&)
  IF n& < 10 THEN
    n& = n& * 2
    Twice n&
  END IF
END SUB
SUB Gauss(BYVAL n&, BYVAL acc&)
  SHARED fh%
  IF n& = 0 THEN
    PRINT #1, acc&
  ELSE
    Gauss n& - 1, acc& + n&
  END IF
END SUB
