' Differential battery 51: SSA across SELECT CASE (conservative multi-way model).
' pb36 now analyzes SELECT-containing bodies (SCCP/DSE) instead of bailing. The
' subject and selector reads are tracked; a value the SELECT might change stays
' unfolded; a constant set on every path still folds. Output byte-identical.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' a constant before a SELECT folds into the arm bodies (loop-invariant style)
k% = 9
sel% = 2
SELECT CASE sel%
  CASE 1
    PRINT #1, k%; 1
  CASE 2
    PRINT #1, k%; 2
  CASE ELSE
    PRINT #1, k%; 0
END SELECT

' a value the SELECT may change is not constant at the merge
r% = 0
SELECT CASE sel%
  CASE 1
    r% = 10
  CASE 2
    r% = 20
  CASE ELSE
    r% = 30
END SELECT
PRINT #1, r%

' EXIT SELECT path
SELECT CASE sel%
  CASE 2
    PRINT #1, "two"
    EXIT SELECT
  CASE ELSE
    PRINT #1, "other"
END SELECT

' SELECT with a range, constant subject reaches arms
v% = 3
SELECT CASE v%
  CASE 1 TO 5
    PRINT #1, "low"; v%
  CASE 6, 7, 8
    PRINT #1, "mid"
  CASE ELSE
    PRINT #1, "other"
END SELECT

CLOSE #1
END
