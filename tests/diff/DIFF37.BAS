' Differential battery 37: O16/O17 - constant-condition branch folding and
' constant-index bounds-check elision must be invisible (an out-of-range
' constant index still raises Error 9 exactly where it would).
$OPTIMIZE SPEED
$ERROR BOUNDS ON
DIM a%(0 TO 10)
%DBG = 0
%REL = 5
OPEN "RESULT.TXT" FOR OUTPUT AS #1
IF 1 THEN PRINT #1, "T1" ELSE PRINT #1, "F1"
IF 0 THEN PRINT #1, "T2" ELSE PRINT #1, "F2"
IF %DBG THEN
  PRINT #1, "dbg"
ELSEIF %REL THEN
  PRINT #1, "rel"
ELSE
  PRINT #1, "none"
END IF
a%(3) = 42
a%(0) = 1
a%(10) = 99
PRINT #1, a%(3); a%(0); a%(10)
i% = 7
a%(i%) = 70
PRINT #1, a%(7)
ON ERROR GOTO trap
bad% = 15
a%(bad%) = 1
PRINT #1, "noerr"
fin:
CLOSE #1
END
trap:
PRINT #1, "err"; ERR
RESUME NEXT
