' @title: TYPE layout ALIGN
' @desc:  ALIGN n pads each field to its natural boundary and rounds the record up; LEN shows the padded size.
TYPE Header ALIGN 4
  tag    AS BYTE
  length AS LONG
  flags  AS INTEGER
END TYPE
DIM h AS Header
h.length = 99
PRINT LEN(h)
PRINT h.length
