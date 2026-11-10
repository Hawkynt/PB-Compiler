' SCREEN 12 GET/PUT byte-layout oracle: genuine QuickBASIC 4.5 vs PB-Compiler.
' Uses published Microsoft Q51412 box plus a two-row, unaligned 9-pixel sprite.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
SCREEN 12
DIM s%(220)

LINE (1, 1)-(10, 3), 4, B
GET (1, 1)-(10, 3), s%(0)
PRINT #1, "BOX";
FOR i% = 0 TO 13
  PRINT #1, s%(i%);
NEXT i%
PRINT #1,

FOR x% = 0 TO 8
  PSET (x%, 5), x%
  PSET (x%, 6), 15 - x%
NEXT x%
GET (0, 5)-(8, 6), s%(0)
PRINT #1, "SPRITE";
FOR i% = 0 TO 9
  PRINT #1, s%(i%);
NEXT i%
PRINT #1,

LINE (31, 31)-(39, 32), 3, BF
PUT (31, 31), s%(0), PSET
PRINT #1, "PSET";
FOR y% = 31 TO 32
  FOR x% = 31 TO 39
    PRINT #1, POINT(x%, y%);
  NEXT x%
NEXT y%
PRINT #1,

LINE (49, 31)-(57, 32), 3, BF
PUT (49, 31), s%(0), XOR
PRINT #1, "XOR";
FOR y% = 31 TO 32
  FOR x% = 49 TO 57
    PRINT #1, POINT(x%, y%);
  NEXT x%
NEXT y%
PRINT #1,

CLOSE #1
SYSTEM
