' @title: TYPE property
' @desc:  PROPERTY GET/SET lift to get_/set_ procedures over a backing field.
TYPE Box
  Count AS INTEGER
  PROPERTY GET Size() AS INTEGER
    Size = THIS.Count
  END PROPERTY
  PROPERTY SET Size(BYVAL n AS INTEGER)
    THIS.Count = n
  END PROPERTY
END TYPE
DIM x AS Box
x.Size = 7
PRINT x.Size
