' @title: Wide integer (INT128)
' @desc:  128-bit integer declaration with addition/subtraction; result narrowed to LONG to print.
DIM a AS INT128
DIM b AS INT128
DIM c AS INT128
DIM lo&
a = 100
b = 23
c = 3
a = a + b
a = a - c
lo& = a
PRINT lo&
