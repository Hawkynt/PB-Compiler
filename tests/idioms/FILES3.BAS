' sequential files: OUTPUT, WRITE #, APPEND, LINE INPUT # and INPUT # to EOF, KILL and DIR$
OPEN "T.TXT" FOR OUTPUT AS #1
PRINT #1, "line one"
WRITE #1, "quoted, text", 42, -1.5
PRINT #1, 1; 2; "three"
CLOSE #1
OPEN "T.TXT" FOR APPEND AS #1
PRINT #1, "appended"
CLOSE
OPEN "T.TXT" FOR INPUT AS #2
LINE INPUT #2, a$
INPUT #2, b$, n%, f!
PRINT a$; "|"; b$; "|"; n%; f!
WHILE NOT EOF(2)
  LINE INPUT #2, c$
  PRINT "["; c$; "]"
WEND
CLOSE #2
KILL "T.TXT"
PRINT DIR$("T.TXT") = ""
