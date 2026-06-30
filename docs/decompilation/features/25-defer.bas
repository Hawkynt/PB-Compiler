' @title: DEFER scope guard
' @desc:  DEFER lowers to a TRY/FINALLY so the deferred statement runs on scope exit.
SUB Work
  DEFER PRINT "cleanup"
  PRINT "working"
END SUB
CALL Work
