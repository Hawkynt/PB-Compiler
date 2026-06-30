' @title: Tuple literal parallel assignment (swap)
' @desc:  a&, b& = (b&, a&) reads every right-hand value into a temp first, giving a simultaneous swap.
DIM a&, b&
a& = 1 : b& = 2
PRINT a&; b&
a&, b& = (b&, a&)
PRINT a&; b&
