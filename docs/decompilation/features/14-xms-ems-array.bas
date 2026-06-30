' @title: XMS / EMS arrays
' @desc:  DIM EMS routes array storage to the EMS-paged heap; access reads as a normal array.
DIM EMS a(1 TO 100) AS LONG
a(1) = 111
a(100) = 999
PRINT a(1)
PRINT a(100)
