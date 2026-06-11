' Differential battery 7: ASCIIZ * n storage and conversions (PB 3.5).
TYPE Rec
  Tag AS ASCIIZ * 8
  Num AS INTEGER
END TYPE

DIM z AS ASCIIZ * 6
DIM r AS Rec

OPEN "RESULT.TXT" FOR OUTPUT AS #1
z = "HelloWorld"           ' truncated to 5 chars + NUL
PRINT #1, z
PRINT #1, LEN(z)
PRINT #1, SIZEOF(z)
z = "Hi"
PRINT #1, z
PRINT #1, LEN(z)
s$ = z + "!"
PRINT #1, s$
r.Tag = "ABC"
r.Num = 42
PRINT #1, r.Tag
PRINT #1, LEN(r.Tag)
PRINT #1, SIZEOF(r.Tag)
PRINT #1, LEN(r)
PRINT #1, r.Num
CLOSE #1
END
