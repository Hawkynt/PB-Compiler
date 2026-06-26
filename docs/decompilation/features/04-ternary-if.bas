' @title: Short-circuit ternary IF()
' @desc:  `IF(cond, whenTrue, whenFalse)` lowers to a real branch; the untaken arm is skipped.
DIM X AS INTEGER
X = 0
PRINT IF(X = 0, 42, 100 \ X)
