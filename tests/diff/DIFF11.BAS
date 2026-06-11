' Differential battery 11: code pointers (PB 3.2) - GOTO DWORD / GOSUB DWORD
' through CODEPTR32 label addresses.
DIM g AS DWORD
OPEN "RESULT.TXT" FOR OUTPUT AS #1

g = CODEPTR32(SubOne)
GOSUB DWORD g
g = CODEPTR32(SubTwo)
GOSUB DWORD g
PRINT #1, "after gosubs"

g = CODEPTR32(Tail)
GOTO DWORD g
PRINT #1, "never printed"

SubOne:
  PRINT #1, "one"
  RETURN

SubTwo:
  PRINT #1, "two"
  RETURN

Tail:
PRINT #1, "tail"
CLOSE #1
END
