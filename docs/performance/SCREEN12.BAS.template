' DOS-native VGA benchmark: run opt and reference on real VGA hardware.
' Elapsed BIOS ticks are approximately 54.925 ms each, not CPU cycles.
$CPU 8086
$COMPILE EXE
SCREEN 12
DIM s%(300)
loops% = 32

DEF SEG = &H40
t0& = CLNG(PEEK(&H6C)) + 256& * CLNG(PEEK(&H6D))
DEF SEG
FOR i% = 1 TO loops%
  LINE (8,20)-(135,51),10,BF
  LINE (8,20)-(135,51),2,BF
NEXT i%
DEF SEG = &H40
t1& = CLNG(PEEK(&H6C)) + 256& * CLNG(PEEK(&H6D))
DEF SEG
bfTicks& = t1& - t0&
IF bfTicks& < 0 THEN bfTicks& = bfTicks& + 65536&

DEF SEG = &H40
t0& = CLNG(PEEK(&H6C)) + 256& * CLNG(PEEK(&H6D))
DEF SEG
FOR i% = 1 TO loops%
  LINE (4,90)-(635,90),14,,&HF0F0
NEXT i%
DEF SEG = &H40
t1& = CLNG(PEEK(&H6C)) + 256& * CLNG(PEEK(&H6D))
DEF SEG
lineTicks& = t1& - t0&
IF lineTicks& < 0 THEN lineTicks& = lineTicks& + 65536&

LINE (8,130)-(71,137),5,BF
GET (8,130)-(71,137),s%(0)
DEF SEG = &H40
t0& = CLNG(PEEK(&H6C)) + 256& * CLNG(PEEK(&H6D))
DEF SEG
FOR i% = 1 TO loops%
  GET (8,130)-(71,137),s%(0)
  PUT (120,160),s%(0),PSET
NEXT i%
DEF SEG = &H40
t1& = CLNG(PEEK(&H6C)) + 256& * CLNG(PEEK(&H6D))
DEF SEG
spriteTicks& = t1& - t0&
IF spriteTicks& < 0 THEN spriteTicks& = spriteTicks& + 65536&

OPEN "VGA.TXT" FOR OUTPUT AS #1
PRINT #1, "iterations";loops%
PRINT #1, "BF-2";bfTicks&
PRINT #1, "styled-LINE";lineTicks&
PRINT #1, "GET-PUT";spriteTicks&
PRINT #1, "pixels";POINT(8,20);POINT(40,90);POINT(120,160);POINT(183,167)
CLOSE #1
SCREEN 0
END
