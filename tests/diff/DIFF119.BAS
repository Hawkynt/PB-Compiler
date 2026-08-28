' Differential battery 119: ON n GOSUB - the dispatched GOSUB, its fall-through,
' and the return stack it shares with a plain GOSUB.
'
' The selector comes out of DATA through READ, so nothing may fold the dispatch.
' 0 and anything past the last label must fall THROUGH to the next statement, and
' every taken arm must RETURN to that same statement. The plain GOSUB written
' after a dispatch is what notices a return address pushed on the path that never
' returns: its own RETURN would come back to the dispatch instead of to itself.
DEFINT A-Z
DECLARE SUB Pick (BYVAL n%)

OPEN "RESULT.TXT" FOR OUTPUT AS #1

' the dispatch inside a procedure, whose labels and return stack are its own
FOR i% = 1 TO 7
  READ n%
  CALL Pick(n%)
NEXT i%

' ...and in the module body, where a plain GOSUB shares the same return stack and
' an arm nests a second one inside the dispatched call
RESTORE
FOR i% = 1 TO 7
  READ n%
  r% = 0
  ON n% GOSUB armA, armB
  GOSUB tail
  PRINT #1, "mix"; n%; r%
NEXT i%
CLOSE #1
END

armA:
  r% = r% + 10
  GOSUB tail
  RETURN

armB:
  r% = r% + 20
  RETURN

tail:
  r% = r% + 1
  RETURN

DATA 0, 1, 2, 3, 4, -1, 32767

SUB Pick (BYVAL n%)
  hit% = 0
  ON n% GOSUB one, two, three
  hit% = hit% + 100
  PRINT #1, "pick"; n%; hit%
  EXIT SUB
one:
  hit% = 1
  RETURN
two:
  hit% = 2
  RETURN
three:
  hit% = 3
  RETURN
END SUB
