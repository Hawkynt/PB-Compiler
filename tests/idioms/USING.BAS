PRINT USING "###.##"; 3.14159
PRINT USING "x=###.## y=#,###,###"; -2.5; 1234567
PRINT USING "##.##"; 0.05; : PRINT USING " #"; 0
PRINT USING "Total: ###"; 5
a$ = USING$("##.#", 2.25)
PRINT "["; a$; "]"
f$ = "<#,###.##>"
b$ = USING$(f$, 9876.543)
PRINT "["; b$; "]"
OPEN "U.TXT" FOR OUTPUT AS #1
PRINT #1, USING "###.#"; 7.25
CLOSE #1
OPEN "U.TXT" FOR INPUT AS #1
LINE INPUT #1, l$
PRINT "["; l$; "]"
CLOSE #1
