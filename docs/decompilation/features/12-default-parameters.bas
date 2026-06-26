' @title: Default parameter values
' @desc:  A trailing parameter with a default may be omitted at the call site.
FUNCTION Pay(x AS INTEGER, y AS INTEGER = 10) AS INTEGER
  Pay = x + y
END FUNCTION
PRINT Pay(5); Pay(5, 20)
