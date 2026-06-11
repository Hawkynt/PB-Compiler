' Differential battery 12: SETEOF truncation, ERRCLEAR, BYVAL pointer override.
DECLARE SUB Bump (v AS INTEGER)

DIM p AS INTEGER PTR

OPEN "RESULT.TXT" FOR OUTPUT AS #1

' SETEOF: write 10 bytes, truncate at the current position
OPEN "SCRATCH.DAT" FOR BINARY AS #2
t$ = "0123456789"
PUT$ #2, t$
SEEK #2, 5
SETEOF #2
CLOSE #2
OPEN "SCRATCH.DAT" FOR BINARY AS #2
PRINT #1, LOF(2)
CLOSE #2
KILL "SCRATCH.DAT"

' ERRCLEAR statement resets ERR inside the handler
ON ERROR GOTO Trap
ERROR 5
Cont:
PRINT #1, "continued"

' BYVAL override: pass the pointer target to a BYREF parameter
x% = 10
p = VARPTR32(x%)
CALL Bump(BYVAL p)
PRINT #1, x%
CLOSE #1
END

Trap:
PRINT #1, ERR
ERRCLEAR
PRINT #1, ERR
RESUME Cont

SUB Bump (v AS INTEGER)
  v = v + 1
END SUB
