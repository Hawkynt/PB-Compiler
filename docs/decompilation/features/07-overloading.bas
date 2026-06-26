' @title: SUB/FUNCTION overloading
' @desc:  Same name with different parameter signatures; each call resolves to the best match.
FUNCTION Area(s AS INTEGER) AS INTEGER
  Area = s * s
END FUNCTION
FUNCTION Area(w AS INTEGER, h AS INTEGER) AS INTEGER
  Area = w * h
END FUNCTION
PRINT Area(4); Area(3, 5)
