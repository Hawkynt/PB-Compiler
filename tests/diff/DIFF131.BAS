' Differential battery 131: a closed file's number is free again - FREEFILE after CLOSE.
' Genuine PBC 3.50 answers 1 1; leaving the handle in its slot answered 4 5.
OPEN "T1.DAT" FOR RANDOM AS #1 LEN = 12
FIELD #1, 4 AS a$, 8 AS b$
LSET a$ = "ab"
PUT #1, 1
CLOSE #1
NAME "T1.DAT" AS "T2.DAT"
OPEN "T2.DAT" FOR BINARY AS #2
CLOSE #2
OPEN "T2.DAT" FOR INPUT AS #3
CLOSE #3
KILL "T2.DAT"
f% = FREEFILE
OPEN "RESULT.TXT" FOR OUTPUT AS #4
PRINT #4, f%; FREEFILE
CLOSE #4
