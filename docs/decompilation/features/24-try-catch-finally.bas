' @title: TRY / CATCH / FINALLY
' @desc:  Structured exception handling lowered onto ON ERROR; FINALLY runs on every path.
DIM x AS INTEGER
TRY
  x = 1 \ 0
CATCH
  PRINT "caught"; ERR
FINALLY
  PRINT "done"
END TRY
