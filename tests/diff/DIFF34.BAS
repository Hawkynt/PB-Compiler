' Differential battery 34: O15 self-copy elision + self-compare folding must
' be invisible. rec = rec is a no-op; rec = rec compares as equal (NaN-immune).
TYPE Pt
  x AS INTEGER
  y AS INTEGER
  tag AS STRING * 4
END TYPE
DIM p AS Pt, q AS Pt
DIM grid(0 TO 3) AS Pt
OPEN "RESULT.TXT" FOR OUTPUT AS #1
p.x = 10
p.y = 20
p.tag = "ABCD"
p = p
PRINT #1, p.x; p.y; p.tag
IF p = p THEN PRINT #1, "self-eq" ELSE PRINT #1, "self-ne"
q.x = 10
q.y = 20
q.tag = "ABCD"
IF p = q THEN PRINT #1, "eq" ELSE PRINT #1, "ne"
q.y = 99
IF p = q THEN PRINT #1, "eq2" ELSE PRINT #1, "ne2"
grid(2).x = 5
grid(2) = grid(2)
PRINT #1, grid(2).x
IF grid(2) = grid(2) THEN PRINT #1, "g-eq"
i% = 42
i% = i%
PRINT #1, i%
s! = 3.5
s! = s!
PRINT #1, s!
CLOSE #1
END
