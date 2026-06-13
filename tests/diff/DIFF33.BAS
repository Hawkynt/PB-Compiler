' Differential battery 33: O3 common subexpression elimination must be
' invisible - repeated pure integer subtrees (y*320+x address arithmetic),
' invalidation on writes, $ERROR NUMERIC overflow at the first occurrence.
DIM scr(0 TO 2000) AS INTEGER
OPEN "RESULT.TXT" FOR OUTPUT AS #1
x% = 7
y% = 3
a% = y% * 320 + x%
b% = y% * 320 + x% + 1
c% = (y% * 320 + x%) * 2
PRINT #1, a%; b%; c%
scr(y% * 320 + x%) = 11
scr(y% * 320 + x% + 1) = 22
PRINT #1, scr(y% * 320 + x%); scr(y% * 320 + x% + 1)
' write to y% invalidates the cached y%*320 subtree
d% = x% * 100 + y%
y% = 5
e% = x% * 100 + y%
PRINT #1, d%; e%
' repeated subtree across a 32-bit accumulator
p& = x% * 1000
q& = x% * 1000 + p&
PRINT #1, p&; q&
' nested CSE: (x%+y%) appears inside two larger trees
f% = (x% + y%) * 3
g% = (x% + y%) * 4
PRINT #1, f%; g%
' barrier (PRINT with same subtree on both sides keeps it alive within stmt)
PRINT #1, x% * 9 + 1; x% * 9 + 1
CLOSE #1
END
