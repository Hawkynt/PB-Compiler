' @title: Nullable type
' @desc:  T? holds a value or NOTHING; ?? coalesces to a default when empty.
DIM age AS INTEGER?
age = 30
PRINT age ?? -1
age = NOTHING
PRINT age ?? -1
