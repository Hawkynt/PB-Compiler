OPEN "RESULT.TXT" FOR OUTPUT AS #1
s$ = "the BEGIN of a BEGINNING, then BEGIN again: ABABABC"
e$ = ""
PRINT #1, INSTR(s$, "BEGIN"); INSTR(s$, "AB"); INSTR(s$, "ABC"); INSTR(s$, "ABABC")
PRINT #1, INSTR(5, s$, "BEGIN"); INSTR(16, s$, "BEGIN"); INSTR(40, s$, "BEGIN"); INSTR(99, s$, "AB")
PRINT #1, INSTR(s$, "NOPE!"); INSTR(s$, "zq"); INSTR(e$, "AB"); INSTR(e$, "BEGIN")
PRINT #1, INSTR(s$, "in"); INSTR(1, s$, "th"); INSTR(2, s$, "BEGINNING,")
t$ = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxy"
PRINT #1, INSTR(t$, "xxxxy"); INSTR(t$, "xy"); INSTR(t$, "yx"); INSTR(LEFT$(t$, 5), "xxxxx")
FOR k% = 1 TO 3
  PRINT #1, INSTR(k% * 7, s$, "BEGIN"); INSTR(k%, s$, "ABAB")
NEXT k%
CLOSE #1
SYSTEM
