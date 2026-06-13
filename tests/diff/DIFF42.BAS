' Differential battery 42: O4 modular 16-bit multiply by a constant.
' Under $OPTIMIZE SPEED, pb36 lowers v% * c into shift/add/sub chains (powers
' of two, two-bit 2^a+2^b, contiguous-run 2^a-2^b, negative via NEG; three-term
' multipliers keep IMUL). Every product is modular 16-bit and must equal the
' genuine IMUL's low word - including the silent wrap on large operands.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

x% = 11
PRINT #1, x% * 0; x% * 1; x% * 2; x% * 3; x% * 4
PRINT #1, x% * 5; x% * 6; x% * 7; x% * 8; x% * 9
PRINT #1, x% * 10; x% * 12; x% * 15; x% * 16; x% * 17
PRINT #1, x% * 100; x% * 1000; x% * 31; x% * 33; x% * 24

' constant on the left
PRINT #1, 3 * x%; 7 * x%; 64 * x%

' negative multipliers (NEG after the magnitude chain)
PRINT #1, x% * -1; x% * -2; x% * -3; x% * -7; x% * -10

' negative operand
n% = -7
PRINT #1, n% * 3; n% * 5; n% * 8; n% * -6; n% * 100

' wrap: large operand times a constant overflows 16 bits silently
big% = 5000
PRINT #1, big% * 3; big% * 7; big% * 13; big% * 16

' the variable is a subtree, not a bare name
PRINT #1, (x% + 1) * 3; (x% - 2) * 5; (n% + big%) * 6

' chained multiplies
PRINT #1, x% * 3 * 5; x% * 6 * 7

CLOSE #1
END
