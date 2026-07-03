' @title: Anonymous types
' @desc:  NEW { .field = value, ... } with no type name synthesizes a UDT from the fields; same shape = same type.
DIM p = NEW { .X = 3, .Y = 4& }
PRINT p.X; p.Y
