' A SINGLE or DOUBLE FUNCTION result is rounded to its declared width on the way out: assigned
' to a DOUBLE, f!(1) with f! = x! / 3 is .333333343267441, not the eighty-bit third.
DECLARE FUNCTION f!(x!)
DECLARE FUNCTION g#(x#)
OPEN "RESULT.TXT" FOR OUTPUT AS #1
d# = f!(1)
PRINT #1, d#
e# = g#(1)
PRINT #1, e#
CLOSE #1
END
FUNCTION f!(x!)
  f! = x! / 3
END FUNCTION
FUNCTION g#(x#)
  g# = x# / 3
END FUNCTION
