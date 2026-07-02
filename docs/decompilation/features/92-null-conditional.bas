' @title: Null-conditional access ?. and ??
' @desc:  obj?.field ?? fallback lowers to IIF(obj.HasValue, obj.Value.field, fallback).
TYPE Point
  X AS LONG
  Y AS LONG
END TYPE
DIM p AS Point?
p.HasValue = -1
p.Value.Y = 7
DIM r AS LONG
r = p?.Y ?? -1
PRINT r
