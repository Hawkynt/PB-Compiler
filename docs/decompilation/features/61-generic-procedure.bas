' @title: Generic procedure (type inferred from arguments)
' @desc:  FUNCTION Max OF T monomorphizes to a concrete Max@Long; T is inferred from the LONG arguments at the call.
FUNCTION Max OF T (BYVAL a AS T, BYVAL b AS T) AS T
  Max = a
  IF b > a THEN
    Max = b
  END IF
END FUNCTION
DIM r&
r& = Max(3&, 9&)
PRINT r&
