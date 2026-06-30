' @title: Manual coroutine driver
' @desc:  A generator call returns its synthesized enumerator; .MoveNext / .Current drive it by hand instead of FOR EACH.
FUNCTION Squares(BYVAL n AS INTEGER) AS LONG
  DIM i AS INTEGER
  FOR i = 1 TO n
    YIELD i * i
  NEXT
END FUNCTION
DIM e AS Squares
e = Squares(5)
DO WHILE e.MoveNext
  PRINT e.Current
LOOP
