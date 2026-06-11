OPEN "RESULT.TXT" FOR OUTPUT AS #1
PRINT #1, LEN("abc"); INSTR("hello", "ll"); ASC("A")
PRINT #1, MID$("abcdef", 2, 3); LEFT$("abc", 2); RIGHT$("abc", 2)
PRINT #1, CHR$(65); STRING$(3, "x"); SPACE$(2); "|"
PRINT #1, UCASE$("aB"); LCASE$("Cd"); LTRIM$("  a"); RTRIM$("b  "); "|"
PRINT #1, SQR(2); EXP(1); LOG(2.718281828459045#)
PRINT #1, SIN(1); COS(1); TAN(1); ATN(1)
PRINT #1, ABS(-7); SGN(-5); SGN(0); SGN(9)
S$ = "abc"
S$ = S$ + "def"
PRINT #1, S$; LEN(S$)
FOR i% = 1 TO 5
  PRINT #1, i%;
NEXT i%
PRINT #1, ""
j% = 0
WHILE j% < 3
  j% = j% + 1
WEND
PRINT #1, j%
k% = 7
SELECT CASE k%
  CASE 1 TO 5
    PRINT #1, "low"
  CASE 6, 7, 8
    PRINT #1, "mid"
  CASE ELSE
    PRINT #1, "high"
END SELECT
DO
  m% = m% + 2
LOOP UNTIL m% >= 6
PRINT #1, m%
GOSUB report
GOTO fin
report:
PRINT #1, "sub"; 1 + 1
RETURN
fin:
n# = 0
FOR x# = 0.1 TO 1.0 STEP 0.3
  n# = n# + x#
NEXT x#
PRINT #1, n#
CLOSE #1
END
