' single-expression FUNCTION inlining: results, argument effects and mixed
' eligible/ineligible callees stay byte-identical to the genuine call chain
DECLARE FUNCTION Triple%(BYVAL x%)
DECLARE FUNCTION Scale&(BYVAL v&, BYVAL k&)
DECLARE FUNCTION Half!(BYVAL f!)
DECLARE FUNCTION Bump%(q%)
OPEN "RESULT.TXT" FOR OUTPUT AS #1
a% = 5
PRINT #1, Triple%(a%); Triple%(2); Triple%(a% + 1)
PRINT #1, Scale&(70000, 3); Scale&(-4, 25000)
PRINT #1, Half!(7)
n% = 0
PRINT #1, Triple%(Bump%(n%)); n%
PRINT #1, Triple%(Triple%(3))
CLOSE #1
END
FUNCTION Triple%(BYVAL x%)
  Triple% = x% * 3
END FUNCTION
FUNCTION Scale&(BYVAL v&, BYVAL k&)
  Scale& = v& * k&
END FUNCTION
FUNCTION Half!(BYVAL f!)
  Half! = f! / 2
END FUNCTION
FUNCTION Bump%(q%)
  q% = q% + 7
  Bump% = q%
END FUNCTION
