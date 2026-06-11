' Differential battery 10: TYPE/UNION whole-value comparison (PB 3.1)
' and the $ELSEIF metastatement (PB 3.5).
' NOTE: genuine PBC 3.50 rejects expressions in $IF (Error 477) - only a
' bare equate (nonzero = true) is accepted; replicated in docs/QUIRKS.md.
%ONE = 0
%TWO = 1
%THREE = 0

TYPE Pair
  A AS INTEGER
  B AS LONG
END TYPE

DIM u AS Pair
DIM v AS Pair

OPEN "RESULT.TXT" FOR OUTPUT AS #1
u.A = 1
u.B = 70000
v.A = 1
v.B = 70000
PRINT #1, u = v
PRINT #1, u <> v
v.B = 70001
PRINT #1, u = v
PRINT #1, u <> v

$IF %ONE
PRINT #1, "one"
$ELSEIF %TWO
PRINT #1, "two"
$ELSEIF %THREE
PRINT #1, "three"
$ELSE
PRINT #1, "other"
$ENDIF
CLOSE #1
END
