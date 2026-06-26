' @title: Named delegate type
' @desc:  A DECLAREd prototype name doubles as a procedure-pointer type used to DIM a variable.
DECLARE FUNCTION Comparator(BYVAL a AS LONG, BYVAL b AS LONG) AS LONG
DIM cmp AS Comparator
cmp = (a, b) => a - b
PRINT cmp(9, 4)
