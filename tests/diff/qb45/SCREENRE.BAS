' Same-mode SCREEN preserves video memory; actual mode changes still clear it.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
SCREEN 13
PSET (10,20),1
PRINT #1,"before";POINT(10,20)
SCREEN 13
PRINT #1,"same";POINT(10,20)
SCREEN 0
SCREEN 13
PRINT #1,"changed";POINT(10,20)
SCREEN 0
DEF SEG = &HB800
POKE 0,90
PRINT #1,"text-before";PEEK(0)
SCREEN 0
PRINT #1,"text-same";PEEK(0)
CLOSE #1
SYSTEM
