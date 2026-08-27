' Differential battery 115: signed LONG ordering where the difference does not fit a LONG.
' A 32-bit ordering decided by the SIGN of (left - right) is right only while that subtraction
' stays in range: -2147483648& - 3& is +2147483645, whose sign says "greater". PBC 3.50 answers
' -1 for -2147483648& < 3&, so the ordering has to come from the subtraction's own overflow-aware
' flags. Every pair below straddles the boundary in one direction or the other; the in-range pairs
' at the end are the control, and they were already right.
' Writes RESULT.TXT; run-diff-tests.sh compares against genuine PBC 3.5 output.
DIM a AS LONG, b AS LONG, i AS INTEGER
OPEN "RESULT.TXT" FOR OUTPUT AS #1

a = -2147483648
b = 3
PRINT #1, (a < b); (a > b); (a <= b); (a >= b); (a = b); (a <> b)
PRINT #1, (b < a); (b > a); (b <= a); (b >= a)

a = -2147483647
b = 3
PRINT #1, (a < b); (a > b); (a <= b); (a >= b)
PRINT #1, (b < a); (b > a); (b <= a); (b >= a)

a = 2147483647
b = -3
PRINT #1, (a < b); (a > b); (a <= b); (a >= b)
PRINT #1, (b < a); (b > a); (b <= a); (b >= a)

a = 2147483647
b = -2147483648
PRINT #1, (a < b); (a > b); (a <= b); (a >= b)
PRINT #1, (b < a); (b > a); (b <= a); (b >= a)

a = -2147483648
b = -2147483648
PRINT #1, (a < b); (a > b); (a <= b); (a >= b); (a = b)

' the same orderings driving a branch rather than a printed truth value
a = -2147483648
b = 3
IF a < b THEN PRINT #1, "lt" ELSE PRINT #1, "not lt"
IF a > b THEN PRINT #1, "gt" ELSE PRINT #1, "not gt"
IF a <= b THEN PRINT #1, "le" ELSE PRINT #1, "not le"
IF a >= b THEN PRINT #1, "ge" ELSE PRINT #1, "not ge"

' a loop whose bound is the boundary value, so the ordering decides how often it runs
a = -2147483646
i = 0
WHILE a < -2147483643
  a = a + 1
  i = i + 1
WEND
PRINT #1, i; a

' the control: pairs whose difference fits, which the sign-of-difference form also got right
a = -1073741824
b = 3
PRINT #1, (a < b); (a > b); (a <= b); (a >= b)
a = 100000
b = 100001
PRINT #1, (a < b); (a > b); (a <= b); (a >= b); (a = b); (a <> b)

CLOSE #1
END
