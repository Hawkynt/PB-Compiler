' Differential battery 134: a bare literal past SINGLE's range is a DOUBLE. 1E50 has two digits,
' which alone would make it SINGLE - and a SINGLE 1E50 is infinite, whose PRINT never finished.
' Genuine PBC 3.50 types it by magnitude as well as by digits.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
PRINT #1, 1E50; -3E40; 1E-50; 2E-39
PRINT #1, 1E300 * 10
x# = 1E200
PRINT #1, x# / 1E100; 3.4E38; 1.5E-38
y! = 1.5E-38
PRINT #1, y!
CLOSE #1
