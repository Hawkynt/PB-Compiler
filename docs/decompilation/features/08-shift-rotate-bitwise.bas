' @title: Shift / rotate / bitwise operators
' @desc:  << >> >>> rotate <<> <>> and bitwise | operate at the left operand's integral width.
DIM n AS INTEGER
n = 1
PRINT n << 3; n | 4
PRINT &H8000 >> 4; &H8000 >>> 4
PRINT &H1 <<> 1; &H1 <>> 1
