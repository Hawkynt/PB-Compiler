' @title: Bracketed collection / range literal
' @desc:  [99..105] is a bracketed range literal that fills the array like {99..105}.
DIM A%() = [99..105]
PRINT A%(0); A%(6)
