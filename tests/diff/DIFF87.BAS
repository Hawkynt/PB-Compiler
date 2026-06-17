' Differential battery 87: O14 general (cross-procedure) tail calls. A SUB whose
' last action is CALL B(args) returns straight to its caller (B reuses the caller
' boundary). Observable results, mutual recursion through two procs, a forwarding
' call with a DIFFERENT argument count, and a deliberately NON-tail call (work
' after the call) must all match the genuine compiler byte for byte.
DECLARE SUB Forward(BYVAL n%)
DECLARE SUB Land(BYVAL a%, BYVAL b%, BYVAL c%)
DECLARE SUB Ping(BYVAL n%)
DECLARE SUB Pong(BYVAL n%)
DECLARE SUB AfterWork(BYVAL n%)
DECLARE SUB Note(BYVAL n%)
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' forwarding tail call where B has MORE args than A (na < nb)
Forward 7

' mutual tail recursion: Ping <-> Pong counts down; the two-proc chain must not
' grow the stack (proven separately by the deep DOSBox test)
Ping 8

' a non-tail call: Note runs AFTER the call, so the call must stay an ordinary
' CALL and the trailing PRINT must still execute
AfterWork 3

CLOSE #1
END

SUB Forward(BYVAL n%)
  ' tail call to a proc with three parameters (na = 2 bytes, nb = 6 bytes)
  Land n%, n% * 2, n% + 100
END SUB

SUB Land(BYVAL a%, BYVAL b%, BYVAL c%)
  PRINT #1, "land"; a%; b%; c%
END SUB

SUB Ping(BYVAL n%)
  PRINT #1, "ping"; n%
  IF n% > 0 THEN Pong n% - 1
END SUB

SUB Pong(BYVAL n%)
  PRINT #1, "pong"; n%
  IF n% > 0 THEN Ping n% - 1
END SUB

SUB AfterWork(BYVAL n%)
  ' the CALL is NOT in tail position - Note prints after it returns
  Note n%
  PRINT #1, "after"; n%
END SUB

SUB Note(BYVAL n%)
  PRINT #1, "note"; n%
END SUB
