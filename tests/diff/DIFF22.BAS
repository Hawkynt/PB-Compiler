' Differential battery 22: LOCAL zero-initialization semantics.
' Locals read before assignment are 0 / empty on EVERY invocation - the pb36
' definite-assignment zero elision (O19) must never change that: SUBs with
' read-before-assign locals keep the frame zeroing, provable ones drop it.
' (real PBC 3.50 requires the explicit empty parameter list on DECLARE)
DECLARE SUB ReadsFirst ()
DECLARE SUB AssignsFirst ()

OPEN "RESULT.TXT" FOR OUTPUT AS #1
CALL ReadsFirst
CALL ReadsFirst
CALL AssignsFirst
CALL AssignsFirst
CLOSE #1
END

SUB ReadsFirst
  ' a% and s$ are deliberately read before assignment - 0 / "" each call
  PRINT #1, a%
  PRINT #1, LEN(s$)
  a% = 7
  s$ = "set"
  PRINT #1, a%; s$
END SUB

SUB AssignsFirst
  ' fully provable: the pb36 frame zeroing collapses to the s$ slot only
  t% = 3
  u& = t% * 2048
  s$ = "v" + STR$(u&)
  PRINT #1, t%; u&; s$
END SUB
