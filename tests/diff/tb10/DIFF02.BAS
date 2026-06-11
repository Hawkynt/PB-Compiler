OPEN "RESULT.TXT" FOR OUTPUT AS #1
PRINT #1, 0.01; 0.001; 0.0001; 0.00001; 0.000001
PRINT #1, 1E-2; 1E-3; 1E-4; 1E-5
PRINT #1, 0.5; -0.25; 100.125; -0.1
PRINT #1, 1/3; 660/256
PRINT #1, "a", "b", "c"
PRINT #1, TAB(10); "x"; SPC(3); "y"
PRINT #1, LEN("abc"); INSTR("hello", "ll"); ASC("A")
PRINT #1, MID$("abcdef", 2, 3); LEFT$("abc", 2); RIGHT$("abc", 2)
PRINT #1, CHR$(65); STRING$(3, "x"); SPACE$(2); "|"
PRINT #1, HEX$(255); OCT$(8); HEX$(-1)
PRINT #1, INT(-1.5); FIX(-1.5); SGN(-5); ABS(-7); CINT(2.5); CINT(3.5)
PRINT #1, SQR(2); EXP(1); LOG(2.718281828459045)
PRINT #1, SIN(1); COS(1); TAN(1); ATN(1)
X% = 5
PRINT #1, X% = 5; X% <> 5; NOT 0
CLOSE #1
END
