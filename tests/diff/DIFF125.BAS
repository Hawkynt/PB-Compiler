' Differential battery 125: DIM DYNAMIC ... AT - an ABSOLUTE array, a VIEW of memory the program
' does not own.
'
' The FORM is what this pins, and it was got wrong first. Genuine PBC 3.50 REFUSES
' `DIM v%(0 TO 3) AT &HB800` outright - "Error 489: Array is already static" - because an array can
' only carry an AT segment if it is dynamic. Our compiler accepted the static spelling, so a program
' written that way compiled here and did not compile there.
'
' ERASE of such an array is deliberately NOT exercised. It has no defined answer to compare: genuine
' terminates the program rather than reporting anything, so a line after it would diff a run against
' one that stopped. docs/DIRECT-EMITTER-RETIREMENT.md records what the oracle said.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
DIM DYNAMIC v%(0 TO 3) AT &HB800
v%(0) = 7
v%(3) = 11
PRINT #1, v%(0); v%(3)
PRINT #1, LBOUND(v%); UBOUND(v%)
CLOSE #1
END
