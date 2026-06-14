' Differential battery 60: cursor column tracking (POS / CSRLIN / LOCATE) vs
' genuine PBC 3.5. POS(0) must follow an explicit LOCATE column and advance as
' screen PRINT emits characters; CSRLIN must report the LOCATE row. Each reading
' is captured before it is written to the file so the result stream is exact.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

LOCATE 5, 20
a% = POS(0) : b% = CSRLIN
PRINT #1, a%; b%

LOCATE 10, 1
a% = POS(0) : b% = CSRLIN
PRINT #1, a%; b%

' keep-row form still sets the column
LOCATE , 40
a% = POS(0)
PRINT #1, a%

' screen PRINT advances the column from the LOCATE origin
LOCATE 1, 1
PRINT "ABCDE";
a% = POS(0)
PRINT #1, a%

' a TAB on screen lands POS on the tab stop
LOCATE 1, 1
PRINT TAB(11);
a% = POS(0)
PRINT #1, a%

CLOSE #1
END
