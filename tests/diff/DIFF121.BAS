' Differential battery 121: the REDIM PRESERVE reshape genuine PBC 3.5 REJECTS.
' PRESERVE may grow the upper bound of the LAST dimension and nothing else. Any other change is
' Error 9 from the RUNTIME, not a compile-time rejection, so the proposed bound is kept
' runtime-valued and the code is read back out of a trap.
' DIFF56 carries the reshape PRESERVE does permit; this program carries the one it does not, and
' the two are separate because ON ERROR GOTO arms every raising statement in the enclosing
' procedure - folding the trap into DIFF56 would recompile that whole battery around a handler it
' was never written to test.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

REDIM p%(1 TO 2, 10 TO 11)
bad% = 3
ON ERROR GOTO PreserveShapeError
REDIM PRESERVE p%(1 TO bad%, 10 TO 11)
PRINT #1, -1
CLOSE #1
END

PreserveShapeError:
PRINT #1, ERR
CLOSE #1
END
