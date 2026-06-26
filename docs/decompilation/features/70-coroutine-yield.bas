' @title: YIELD generator
' @desc:  A FUNCTION containing YIELD becomes a generator consumed by FOR EACH.
FUNCTION Squares(BYVAL n AS INTEGER) AS LONG
  DIM i AS INTEGER
  FOR i = 1 TO n
    YIELD i * i
  NEXT
END FUNCTION
DIM v AS LONG
FOR EACH v IN Squares(4)
  PRINT v
NEXT
