' Genuine Microsoft QuickBASIC 4.5 reference for SCREEN 12 raster/PRINT.
' Samples both geometry and opaque text through ordinary BIOS-backed POINT.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
SCREEN 12

LINE (20, 20)-(60, 40), 2, BF
LINE (20, 20)-(60, 40), 6, , &HAAAA
LINE (60, 40)-(20, 20), 5, , &HF0F0
CIRCLE (120, 100), 22, 9, , , 1.5
PRINT #1, "RASTER";
FOR y% = 15 TO 135 STEP 7
  FOR x% = 15 TO 170 STEP 7
    PRINT #1, POINT(x%, y%);
  NEXT x%
NEXT y%
PRINT #1,

LINE (8, 16)-(15, 31), 3, BF
COLOR 14
LOCATE 2, 2
PRINT "A";
PRINT #1, "TEXT";
FOR y% = 16 TO 31
  FOR x% = 8 TO 15
    PRINT #1, POINT(x%, y%);
  NEXT x%
NEXT y%
PRINT #1,

CLOSE #1
SYSTEM
