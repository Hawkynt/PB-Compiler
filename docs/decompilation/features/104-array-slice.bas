' @title: Array slices
' @desc:  b() = a(lo TO hi) copies a slice into a dynamic array (REDIM + loop); FOR EACH iterates one; bounds omissible or from-end (^n).
DIM a(1 TO 8) AS INTEGER
DIM i AS INTEGER
FOR i = 1 TO 8
  a(i) = i * 10
NEXT
DIM b() AS INTEGER
b() = a(3 TO ^2)
PRINT b(0); UBOUND(b)
DIM v AS INTEGER
FOR EACH v IN a(6 TO)
  PRINT v;
NEXT
