' $LINK demo: cross-unit calls into MATHUNIT.PBU - numeric result, BYREF
' mutation through a unit SUB, string building via the runtime, nesting.
$LINK "MATHUNIT.PBU"
DECLARE FUNCTION AddInts%(BYVAL a%, BYVAL b%)
DECLARE SUB Bump(x%)
DECLARE FUNCTION Greet$(name$)

PRINT AddInts%(2, 3)
n% = 35
CALL Bump(n%)
PRINT n%
PRINT Greet$("UNIT")
PRINT AddInts%(AddInts%(1, 2), 4)
END
