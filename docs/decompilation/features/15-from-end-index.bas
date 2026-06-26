' @title: From-end array index
' @desc:  arr(^1) is the last element; the binder rewrites it to UBOUND(arr) - n + 1.
DIM A(1 TO 3) AS INTEGER
A(1) = 10 : A(2) = 20 : A(3) = 30
PRINT A(^1)
