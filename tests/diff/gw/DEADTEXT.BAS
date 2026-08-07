10 REM a line control jumps OVER is never parsed; a dead clause on a live LINE still is
20 OPEN "RESULT.TXT" FOR OUTPUT AS #1
30 IF -1 GOTO 50
40 THIS IS ARBITRARY TEXT
50 PRINT #1, "dead interpreter text skipped"
60 CLOSE #1
70 SYSTEM
