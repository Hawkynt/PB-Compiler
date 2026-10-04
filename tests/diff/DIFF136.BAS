' Differential battery 136: the error a failed OPEN raises is the one its cause names - file not found 53,
' path not found 76 - not the device I/O error 57 for every one; ERRCLEAR answers ERR and clears it, and
' ON ERROR GOTO 0 clears it too. ON ERROR RESUME NEXT leaves ERR alone across statements that succeed.
OPEN "RESULT.TXT" FOR OUTPUT AS #9
ON ERROR RESUME NEXT
OPEN "nonexist.dat" FOR INPUT AS #1
PRINT #9, "a"; ERR
OPEN "NOPATH\X.DAT" FOR INPUT AS #1
PRINT #9, "b"; ERR
OPEN "NOPATH\X.DAT" FOR OUTPUT AS #1
PRINT #9, "c"; ERR
OPEN "nonexist.dat" FOR BINARY AS #1
PRINT #9, "d"; ERR
CLOSE #1
KILL "nonexist.dat"
OPEN "nonexist.dat" FOR RANDOM AS #1
PRINT #9, "e"; ERR
CLOSE #1
KILL "nonexist.dat"
KILL "nonexist.dat"
PRINT #9, "f"; ERR
x = ERRCLEAR
PRINT #9, "g"; x; ERR
ERROR 5
PRINT #9, "h"; ERR
ON ERROR GOTO 0
PRINT #9, "i"; ERR
CLOSE #9
