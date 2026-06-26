' @title: Scaled pointer arithmetic
' @desc:  ptr +* index / ptr -* index move a typed pointer by index * sizeof(target).
DIM a(2) AS INTEGER
a(0) = 10 : a(1) = 20 : a(2) = 30
DIM p AS INTEGER PTR
p = VARPTR(a(0))
p = p +* 2
PRINT @p
p = p -* 1
PRINT @p
