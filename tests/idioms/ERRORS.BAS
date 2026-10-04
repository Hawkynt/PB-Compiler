' error handling: ON ERROR GOTO with ERR and ERL, RESUME NEXT, ERROR n, ON ERROR RESUME NEXT around a failing OPEN
ON ERROR GOTO Handler
10 x% = 32767
20 x% = x% + 1
30 PRINT "after"; x%
ERROR 5
PRINT "back"
ON ERROR RESUME NEXT
OPEN "nonexist.dat" FOR INPUT AS #1
PRINT "err"; ERR
ON ERROR GOTO 0
PRINT ERRCLEAR
END
Handler:
PRINT "error"; ERR; "at"; ERL
RESUME NEXT
