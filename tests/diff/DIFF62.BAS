' Differential battery 62: dense integer SELECT CASE. Under pb36 this lowers to a
' jump table (single-value constant cases, dense span); the same arm must run as the
' pb35 compare chain, so the output is byte-identical under both.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
FOR i% = -3 TO 12
  SELECT CASE i%
  CASE 1
    PRINT #1, "one"
  CASE 2
    PRINT #1, "two"
  CASE 3
    PRINT #1, "three"
  CASE 5
    PRINT #1, "five"; i%
  CASE 7
    PRINT #1, "seven"
  CASE 8
    PRINT #1, "eight"
    EXIT SELECT
    PRINT #1, "unreached"
  CASE ELSE
    PRINT #1, "other"; i%
  END SELECT
NEXT i%
CLOSE #1
END
