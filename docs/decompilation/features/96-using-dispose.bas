' @title: USING (scope-guaranteed Dispose)
' @desc:  USING v AS Type(args) constructs and schedules v.Dispose() via DEFER - TRY/FINALLY guarantees it on the fault path too.
TYPE Res
  Handle AS LONG
  SUB Res(BYVAL h AS LONG)
    THIS.Handle = h
  END SUB
  SUB Dispose()
    PRINT "disposed"; THIS.Handle
  END SUB
END TYPE
SUB Work()
  USING r AS Res(42)
  PRINT "using"; r.Handle
END SUB
Work
