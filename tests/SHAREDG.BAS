' SHARED module state seen by both the main body and a procedure: one cell, not
' one per scope - and a STATIC local that must survive between calls.
DECLARE SUB Bump()
DECLARE FUNCTION Ticks%()
DIM g AS SHARED INTEGER
DIM tally(3) AS SHARED INTEGER
g = 5
Bump
Bump
tally(2) = g
PRINT "g="; g; " tally1="; tally(1); " tally2="; tally(2)
PRINT "ticks="; Ticks%; Ticks%; Ticks%
END

SUB Bump()
  g = g + 10
  tally(1) = tally(1) + 1
END SUB

FUNCTION Ticks%()
  STATIC seen AS INTEGER
  seen = seen + 1
  Ticks% = seen
END FUNCTION
