' @title: Tuple return + destructuring
' @desc:  A FUNCTION AS (LONG, LONG) returns an anonymous tuple by struct return; q&, r& = DivMod(...) destructures its Item1/Item2 fields.
FUNCTION DivMod(BYVAL a AS LONG, BYVAL b AS LONG) AS (LONG, LONG)
  DivMod.Item1 = a \ b
  DivMod.Item2 = a MOD b
END FUNCTION
DIM q&, r&
q&, r& = DivMod(17, 5)
PRINT q&; r&
