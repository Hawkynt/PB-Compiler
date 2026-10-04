' Differential battery 135: DIR$(mask$) finds the first match and a bare DIR$ the next; the empty string ends
' the search, and a mask nothing matches answers it at once. It refused to compile before.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
OPEN "ZQA1.DAT" FOR OUTPUT AS #2: CLOSE #2
OPEN "ZQB2.DAT" FOR OUTPUT AS #2: CLOSE #2
f$ = DIR$("ZQ*.DAT")
n% = 0
WHILE LEN(f$)
  INCR n%
  IF f$ = "ZQA1.DAT" OR f$ = "ZQB2.DAT" THEN PRINT #1, "found"; n%
  f$ = DIR$
WEND
PRINT #1, "["; DIR$("NOSUCH.XYZ"); "]"
KILL "ZQA1.DAT": KILL "ZQB2.DAT"
PRINT #1, "["; DIR$("ZQ*.DAT"); "]"
CLOSE #1
