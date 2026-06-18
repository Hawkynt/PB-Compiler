' Differential battery 111: the screen column (POS(0)) is independent of file PRINT columns.
' A PRINT #n to a file advances that file's own comma-zone/TAB column but must NOT disturb the
' screen cursor column that POS(0) reads, nor another file's column. Genuine PBC 3.50 keeps a
' separate column per output; each POS reading is captured before it is written.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' a screen column is set, a file PRINT runs, POS(0) must still read the SCREEN column
LOCATE 1, 1
PRINT "ABCDE";
PRINT #1, "0123456789";
a% = POS(0)
PRINT #1, ""
PRINT #1, "scr"; a%

' LOCATE sets the screen column; an intervening file PRINT does not overwrite it
LOCATE 4, 12
PRINT #1, "wxyz";
a% = POS(0)
PRINT #1, ""
PRINT #1, "loc"; a%

' the file's own comma zones advance by the file column (a TAB lands on the stop)
PRINT #1, "p", "q", "r"
PRINT #1, "ab"; TAB(20); "cd"

CLOSE #1
END
