' @title: FOR EACH
' @desc:  FOR EACH v IN array lowers to a counted index loop copying each element into v.
DIM A(1 TO 3) AS INTEGER
A(1) = 10 : A(2) = 20 : A(3) = 30
DIM V AS INTEGER
FOR EACH V IN A
  PRINT V
NEXT
