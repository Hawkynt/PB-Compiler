' Differential battery 63: dense LONG (32-bit) SELECT CASE. Under pb36 this lowers to a
' jump table over a 32-bit subject (Sub/Sbb index, two-step range check); the same arm
' must run as the pb35 compare chain, so the output is byte-identical under both.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
DIM v AS LONG
FOR k% = -2 TO 9
  v = 100000 + k%
  SELECT CASE v
  CASE 100000
    PRINT #1, "zero"
  CASE 100001
    PRINT #1, "one"
  CASE 100002
    PRINT #1, "two"
  CASE 100004
    PRINT #1, "four"; v
  CASE 100005
    PRINT #1, "five"
    EXIT SELECT
    PRINT #1, "unreached"
  CASE ELSE
    PRINT #1, "other"; v
  END SELECT
NEXT k%
CLOSE #1
END
