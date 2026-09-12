' Differential battery 124: REDIM of an array PARAMETER reallocates the CALLER's array.
' The descriptor a callee receives is the caller's own, so a REDIM inside the callee is not a
' private rearrangement: the caller sees the new bounds afterwards, and without PRESERVE it sees a
' cleared array as well. Genuine PBC 3.50 answers 1 9 and 0 42 here.
'
' This is worth pinning because the failure mode is silent. Recording the new block in a cell the
' caller cannot see leaves UBOUND answering the OLD bound while an element write lands in the NEW
' block - so the program keeps running and prints plausible numbers, which is what it did before.
SUB Grow(a%())
  REDIM a%(1 TO 9)
  a%(9) = 42
END SUB

OPEN "RESULT.TXT" FOR OUTPUT AS #1
REDIM v%(1 TO 2)
v%(1) = 7
Grow v%()
PRINT #1, LBOUND(v%); UBOUND(v%)
PRINT #1, v%(1); v%(9)
CLOSE #1
END
