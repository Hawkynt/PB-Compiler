' Differential battery 57: file I/O vs genuine PBC 3.5.
' Sequential WRITE/PRINT then INPUT#/LINE INPUT# read-back, APPEND, EOF/LOF/LOC,
' and binary GET/PUT - the read-back values written to RESULT.TXT must match.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' ---- sequential WRITE # then numeric INPUT # read-back ---------------------
OPEN "DATA1.TXT" FOR OUTPUT AS #2
WRITE #2, 42, 3.5
WRITE #2, -7, 2.25
CLOSE #2

OPEN "DATA1.TXT" FOR INPUT AS #2
n% = 0 : f! = 0
INPUT #2, n%, f!
PRINT #1, n%; f!
INPUT #2, n%, f!
PRINT #1, n%; f!
PRINT #1, EOF(2)
CLOSE #2

' ---- APPEND then LINE INPUT # ----------------------------------------------
OPEN "DATA2.TXT" FOR OUTPUT AS #3
PRINT #3, "line one"
CLOSE #3
OPEN "DATA2.TXT" FOR APPEND AS #3
PRINT #3, "line two"
CLOSE #3
OPEN "DATA2.TXT" FOR INPUT AS #3
LINE INPUT #3, l1$
LINE INPUT #3, l2$
PRINT #1, l1$; "|"; l2$
PRINT #1, EOF(3)
CLOSE #3

' ---- LOF / count lines -----------------------------------------------------
OPEN "DATA2.TXT" FOR INPUT AS #3
cnt% = 0
DO WHILE NOT EOF(3)
  LINE INPUT #3, t$
  INCR cnt%
LOOP
PRINT #1, cnt%
CLOSE #3

' ---- binary PUT / GET ------------------------------------------------------
OPEN "DATA3.BIN" FOR BINARY AS #4
v% = 12345
PUT #4, 1, v%
w% = 0
GET #4, 1, w%
PRINT #1, w%
CLOSE #4

CLOSE #1
END
