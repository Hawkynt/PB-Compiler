' @title: DEF SEG coalescing (O10)
' @desc:  A DEF SEG with only segment-transparent statements before the next DEF SEG is redundant and dropped.
DIM X AS INTEGER
DEF SEG = &HB800
X = 5
DEF SEG = &HA000
POKE 0, 65
