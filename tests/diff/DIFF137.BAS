' Differential battery 137: file errors as genuine PBC 3.50 numbers them - PRINT # or LOF of a closed number 52,
' OPEN of a number in use 55, LINE INPUT # and INPUT # past the end 62, NAME of a missing file 53, MKDIR of
' a directory that exists 75, RMDIR and CHDIR of one that does not 76. CLOSE of a closed number is no error.
OPEN "RESULT.TXT" FOR OUTPUT AS #9
ON ERROR RESUME NEXT
x = ERRCLEAR
PRINT #5, "x"
PRINT #9, "a"; ERRCLEAR
CLOSE #6
PRINT #9, "b"; ERRCLEAR
y& = LOF(7)
PRINT #9, "c"; ERRCLEAR
OPEN "Q.DAT" FOR OUTPUT AS #1
OPEN "Q.DAT" FOR OUTPUT AS #1
PRINT #9, "d"; ERRCLEAR
CLOSE #1
OPEN "Q.DAT" FOR INPUT AS #1
LINE INPUT #1, a$
PRINT #9, "e"; ERRCLEAR
CLOSE #1
KILL "Q.DAT"
NAME "NONE.DAT" AS "NONE2.DAT"
PRINT #9, "f"; ERRCLEAR
MKDIR "ZQDIR"
MKDIR "ZQDIR"
PRINT #9, "g"; ERRCLEAR
RMDIR "ZQDIR"
RMDIR "ZQDIR"
PRINT #9, "h"; ERRCLEAR
CHDIR "NOWHERE"
PRINT #9, "i"; ERRCLEAR
OPEN "Q2.DAT" FOR OUTPUT AS #1
PRINT #1, "one"
CLOSE #1
OPEN "Q2.DAT" FOR INPUT AS #1
INPUT #1, a$
PRINT #9, "j"; ERRCLEAR; a$
INPUT #1, a$
PRINT #9, "k"; ERRCLEAR
CLOSE #1
KILL "Q2.DAT"
CLOSE #9
