' String literals keep every byte: CP437 text above 127 (box drawing, accents) reaches the
' image, ASC and INSTR as the byte it was in the source, not as a '?'.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
PRINT #1, "caf‚ ÉÍÍ» °±²Û"
PRINT #1, ASC("‚"); ASC("ÿ"); LEN("ÉÍ»")
b$ = "É" + STRING$(3, 205) + "»"
PRINT #1, b$; INSTR(b$, "Í»"); (b$ = CHR$(201, 205, 205, 205, 187))
PRINT #1, ("‚" = CHR$(130)); ("‚" > "z")
CLOSE #1
END
