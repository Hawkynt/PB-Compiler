' memory by segment and offset: DEF SEG with PEEK, POKE and PEEK$/POKE$, an array laid over the same
' bytes with DIM AT, and HUGE and VIRTUAL arrays - every route to one byte agreeing about it
DEF SEG = &H40
FOR i% = 0 TO 7
  POKE &H100 + i%, 65 + i%
NEXT
PRINT PEEK(&H100); PEEK(&H107); PEEKI(&H100); PEEKL(&H100)
PRINT PEEK$(&H100, 8)
POKE$ &H108, "xyz"
PRINT PEEK$(&H106, 5)
DEF SEG

DIM DYNAMIC vw?(0 TO 15) AT &H50
PRINT vw?(0); vw?(7); CHR$(vw?(8))
vw?(15) = 99
DEF SEG = &H50
PRINT PEEK(15)
DEF SEG

DIM HUGE big&(1 TO 3000)
FOR i& = 1 TO 3000 STEP 97
  big&(i&) = i& * 3
NEXT
t& = 0
FOR i& = 1 TO 3000
  t& = t& + big&(i&)
NEXT
PRINT t&; LBOUND(big&); UBOUND(big&)
ERASE big&

DIM VIRTUAL v%(1 TO 2000)
v%(1) = 11: v%(2000) = 2000
PRINT v%(1) + v%(2000)
