' @title: Interpolated strings
' @desc:  `$"...{expr}..."` lowers to literal + STR$/USING$ concatenation.
DIM N AS INTEGER
N = 42
DIM S AS STRING
S = $"n={N} hex={HEX$(N)}"
PRINT S
