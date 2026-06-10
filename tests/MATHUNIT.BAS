' $COMPILE UNIT: compiled to MATHUNIT.PBU by the harness, linked by LINKDEMO.BAS.
' Exercises exported FUNCTION/SUB, BYREF mutation and runtime string imports.
$COMPILE UNIT

FUNCTION AddInts%(BYVAL a%, BYVAL b%)
  AddInts% = a% + b%
END FUNCTION

SUB Bump(x%)
  x% = x% + 7
END SUB

FUNCTION Greet$(name$)
  Greet$ = "HI " + name$ + "!"
END FUNCTION
