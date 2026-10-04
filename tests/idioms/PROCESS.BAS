' the process and text: COMMAND$, ENVIRON$, STRING$/SPACE$, the MID$ statement, INSTR, case, trimming, REPEAT$, SWAP of strings
PRINT LEN(COMMAND$)
PRINT ENVIRON$("NOSUCHVAR_PB") = ""
x$ = STRING$(3, "*") + SPACE$(2) + "|"
PRINT x$
MID$(x$, 2, 1) = "#"
PRINT x$; LEFT$(x$, 2); RIGHT$(x$, 2)
PRINT INSTR(2, "abcabc", "c"); UCASE$("mixed Case"); LCASE$("MiX")
PRINT LTRIM$("  a "); RTRIM$(" b  "); "|"; TRIM$("  c  "); "|"
a$ = "x": b$ = "y": SWAP a$, b$: PRINT a$; b$
PRINT CHR$(65, 66, 67); ASC("A"); STR$(-5); STR$(5)
