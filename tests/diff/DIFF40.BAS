' Differential battery 40: R3 - the DWORD-wide string copy under $CPU 80386
' must produce byte-identical strings to the genuine REP MOVSB copy across
' literal assignment (StrMem), concatenation, slicing and long buffers.
$CPU 80386
OPEN "RESULT.TXT" FOR OUTPUT AS #1
a$ = "Hello, World!"
b$ = a$ + " And more."
PRINT #1, a$; "|"; b$; LEN(b$)
c$ = "0123456789ABCDEF"
PRINT #1, MID$(c$, 3, 7); LEFT$(c$, 4); RIGHT$(c$, 5)
d$ = STRING$(30, "x") + STRING$(17, "y")
PRINT #1, d$; LEN(d$)
e$ = ""
FOR i% = 1 TO 9
  e$ = e$ + CHR$(48 + i%)
NEXT i%
PRINT #1, e$
IF a$ = "Hello, World!" THEN PRINT #1, "eq" ELSE PRINT #1, "ne"
f$ = SPACE$(13) + "tail"
PRINT #1, "["; f$; "]"; LEN(f$)
CLOSE #1
END
