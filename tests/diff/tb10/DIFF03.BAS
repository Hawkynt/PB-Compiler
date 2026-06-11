DEF FNDouble(x) = x * 2
OPEN "RESULT.TXT" FOR OUTPUT AS #1
FOR i% = 1 TO 5
  PRINT #1, i%;
NEXT i%
PRINT #1, ""
j% = 0
WHILE j% < 3
  j% = j% + 1
WEND
PRINT #1, j%
k% = 7
SELECT CASE k%
  CASE 1 TO 5
    PRINT #1, "low"
  CASE 6, 7, 8
    PRINT #1, "mid"
  CASE ELSE
    PRINT #1, "high"
END SELECT
DO
  m% = m% + 2
LOOP UNTIL m% >= 6
PRINT #1, m%
PRINT #1, FNDouble(21)
GOSUB report
GOTO fin
report:
PRINT #1, "sub"; 1 + 1
RETURN
fin:
n# = 0
FOR x# = 0.1 TO 1.0 STEP 0.3
  n# = n# + x#
NEXT x#
PRINT #1, n#
CLOSE #1
END
