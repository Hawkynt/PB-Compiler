' @title: TYPE explicit AT overlay
' @desc:  field AS T AT offset overlays fields to form a union view; setting the whole exposes its parts.
TYPE RegView
  whole AS LONG
  lo    AS INTEGER AT 0
  hi    AS INTEGER AT 2
END TYPE
DIM v AS RegView
v.whole = &H00050003
PRINT v.lo
PRINT v.hi
