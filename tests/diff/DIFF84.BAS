' Differential battery 84: ON ERROR RESUME NEXT (inline error mode).
OPEN "RESULT.TXT" FOR OUTPUT AS #1
ON ERROR RESUME NEXT

' a faulting statement is skipped, ERR set, execution continues at the next
ERROR 5
PRINT #1, "a "; ERR

' a second fault overwrites ERR (inline mode does not auto-clear)
ERROR 7
PRINT #1, "b "; ERR

' another raised fault resumes next, ERR reflects the latest code
ERROR 11
PRINT #1, "c "; ERR

' ERRCLEAR resets ERR to 0 inside inline mode
ERRCLEAR
PRINT #1, "d "; ERR

' a non-faulting statement leaves ERR untouched
DIM z AS INTEGER
z = 3
PRINT #1, "e "; ERR; z

' ERL stays 0 - no numeric line labels were executed
PRINT #1, "g "; ERL

' turning the handler off restores fatal behaviour - but we end before faulting
ON ERROR GOTO 0
PRINT #1, "f done"
CLOSE #1
END
