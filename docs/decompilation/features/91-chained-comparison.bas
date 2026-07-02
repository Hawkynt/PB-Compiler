' @title: Chained comparison
' @desc:  lo <= x < hi desugars to (lo <= x) AND (x < hi), reusing the middle operand.
DIM i AS INTEGER
i = 5
DIM n AS INTEGER
n = 10
IF 0 <= i < n THEN
  PRINT "in range"
END IF
