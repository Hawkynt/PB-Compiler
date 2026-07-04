' @title: Array construction with slice spreads
' @desc:  ..b(lo TO hi) spreads a slice; bounds may be omitted (LBOUND/UBOUND) or from-end (^n).
DIM b(4) AS INTEGER
DIM i AS INTEGER
FOR i = 0 TO 4
  b(i) = 10 + i
NEXT
DIM a = {1, ..b(1 TO 2), 5 TO 7, ..b(^2 TO)}
FOR i = LBOUND(a) TO UBOUND(a)
  PRINT a(i);
NEXT
