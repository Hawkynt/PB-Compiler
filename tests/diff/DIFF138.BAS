' Differential battery 138: PowerBASIC's CASE has no IS. CASE > 15 is the relation; CASE IS > 15 is the case
' value (IS > 15) with IS a plain variable, zero, so it matches nothing above 15. Genuine PBC 3.0 and 3.50 agree.
OPEN "RESULT.TXT" FOR OUTPUT AS #9
FOR v% = 10 TO 20 STEP 2
SELECT CASE v%
  CASE IS > 15, 12: PRINT #9, v%; "a"
  CASE ELSE: PRINT #9, v%; "-"
END SELECT
SELECT CASE v%
  CASE 12, IS > 15: PRINT #9, v%; "b"
  CASE ELSE: PRINT #9, v%; "-"
END SELECT
SELECT CASE v%
  CASE IS > 15: PRINT #9, v%; "c"
  CASE ELSE: PRINT #9, v%; "-"
END SELECT
SELECT CASE v%
  CASE 1 TO 9, IS >= 18: PRINT #9, v%; "d"
  CASE ELSE: PRINT #9, v%; "-"
END SELECT
NEXT
FOR v% = 14 TO 18 STEP 2
SELECT CASE v%
  CASE IS > 15
    PRINT #9, v%; "c"
  CASE ELSE
    PRINT #9, v%; "-"
END SELECT
SELECT CASE v%
  CASE > 15
    PRINT #9, v%; "e"
  CASE ELSE
    PRINT #9, v%; "-"
END SELECT
x% = v%
SELECT CASE x%
  CASE IS > 15: PRINT #9, x%; "f"
  CASE ELSE: PRINT #9, x%; "-"
END SELECT
NEXT
v% = 16
SELECT CASE v%
  CASE IS > 15: PRINT #9, v%; "g"
END SELECT
CLOSE #9
