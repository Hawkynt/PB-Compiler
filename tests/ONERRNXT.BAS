' ON ERROR RESUME NEXT - inline error mode: a faulting statement is skipped,
' ERR is left set until the next fault or ERRCLEAR, execution continues next.
ON ERROR RESUME NEXT
ERROR 5
PRINT "a"; ERR
ERROR 7
PRINT "b"; ERR
ERRCLEAR
PRINT "c"; ERR
PRINT "d"; ERL
ON ERROR GOTO 0
PRINT "done"
END
