' Differential battery 68: broader GVN - flow a common subexpression PAST a SELECT join.
' Like the IF merge (DIFF67), a value computed before a SELECT whose arms (all flat and
' call-free) do not write its inputs stays live across the join and reloads after it; an
' arm that overwrites an input forces a recompute. Byte-identical to pb35 and PBC 3.50.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

y% = 4 : x% = 2

' y%*320 (input y%) computed before the SELECT; arms touch only w% so it survives
m% = y% * 320 + x%
SELECT CASE x%
CASE 1
  w% = 10
  PRINT #1, "c1"; w%
CASE 2
  w% = 20
  PRINT #1, "c2"; w%
CASE ELSE
  w% = 30
  PRINT #1, "ce"; w%
END SELECT
n% = y% * 320 + x%
PRINT #1, "keep"; m%; n%

' contrast: an arm overwrites y%, an input of the cached multiply, so it recomputes
y% = 4 : x% = 7
g% = y% * 320 + x%
SELECT CASE x%
CASE 7
  y% = 11
CASE ELSE
  w% = 0
END SELECT
h% = y% * 320 + x%
PRINT #1, "ovr"; g%; h%

CLOSE #1
END
