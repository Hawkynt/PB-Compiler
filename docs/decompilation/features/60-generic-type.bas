' @title: Generic TYPE
' @desc:  TYPE Box OF T monomorphizes to a concrete TYPE per concrete instantiation.
TYPE Box OF T
  Item AS T
  SUB Put(BYVAL v AS T)
    THIS.Item = v
  END SUB
END TYPE
DIM b AS Box OF LONG
b.Put(42)
PRINT b.Item
