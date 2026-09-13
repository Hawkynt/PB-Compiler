' Differential battery 127: GET / PUT of a STRING variable.
'
' A string is a HANDLE, not a record of its own size, so these move the heap bytes rather than the
' cell - PB reads LEN(s$) bytes into the string it is given, which is why the target is padded to
' the width first. The routed path had no name for the pair the runtime already had, so a program
' parsing a file header into a string went to the direct emitter entire.
OPEN "RESULT.TXT" FOR OUTPUT AS #2
DIM s AS STRING, t AS STRING
s = "HDRX"
OPEN "GP.TMP" FOR BINARY AS #1
PUT #1, , s
CLOSE #1
t = "    "
OPEN "GP.TMP" FOR BINARY AS #1
GET #1, , t
CLOSE #1
PRINT #2, t
PRINT #2, LEN(t)
CLOSE #2
KILL "GP.TMP"
END
