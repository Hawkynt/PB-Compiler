' Differential battery 21: CHAIN with COMMON handoff. The program chains to
' itself once; the second pass sees the first pass's COMMON values (genuine
' PBC carries them in memory, PB-Compiler through the PBCHAIN.$$$ temp file -
' the observable behavior is identical).
COMMON stage%, n&, d#, msg$
OPEN "RESULT.TXT" FOR APPEND AS #1
IF stage% = 0 THEN
  stage% = 1
  n& = 123456
  d# = 2.5
  msg$ = "hello chain"
  PRINT #1, "first pass"
  CLOSE #1
  CHAIN "T.EXE"
  PRINT #1, "not reached"
END IF
PRINT #1, "second pass"
PRINT #1, stage%; n&; d#
PRINT #1, msg$
CLOSE #1
END
