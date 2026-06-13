' Differential battery 43: O8 modular 16-bit add/sub by a constant.
' pb36 folds v% +/- c into one immediate ALU op (v - c adds -c; c - v negates
' then adds c). All arithmetic is modular 16-bit and must equal the generic
' ADD/SUB path, wrap included. Runs unchecked (no $ERROR), so no overflow trap.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

x% = 100
PRINT #1, x% + 0; x% + 1; x% + 7; x% + 255; x% + 1000
PRINT #1, x% - 1; x% - 7; x% - 200; x% - 1000
PRINT #1, 1 + x%; 50 + x%; 1000 + x%
PRINT #1, 50 - x%; 1000 - x%; 0 - x%

' negatives and wrap
n% = -30000
PRINT #1, n% - 5000; n% + 5000; n% - 10000
big% = 30000
PRINT #1, big% + 5000; big% + 3000; 5000 - big%

' const add/sub as a subtree feeding a multiply (modular nesting)
PRINT #1, (x% + 1) * 4; (x% - 5) * 3; (10 - x%) * 2
PRINT #1, (x% + 3) - 1; (x% - 2) + 8

CLOSE #1
END
