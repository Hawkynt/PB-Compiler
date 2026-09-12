' Differential battery 126: ERASE of an ABSOLUTE array, up to the point genuine still answers.
'
' ERASE cannot free or zero an ABSOLUTE array - the memory is not the program's - so the only thing
' it undoes is the VIEW. What is pinned here is that the ERASE itself is accepted and that execution
' continues past it; what deliberately is NOT pinned is a later access, because genuine PBC 3.50
' terminates the program there rather than answering, and a run that stopped cannot be diffed.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
DIM DYNAMIC v%(0 TO 3) AT &HB800
v%(0) = 7
v%(2) = 9
PRINT #1, v%(0); v%(2)
ERASE v%
PRINT #1, "erased"
CLOSE #1
END
