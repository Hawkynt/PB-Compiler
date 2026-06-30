' @title: TYPE bit-field members
' @desc:  AS BIT * n packs fields into a hidden $bits WORD; each write is a mask-preserving read-modify-write.
TYPE StatusReg
  Mode    AS BIT * 3
  Enabled AS BIT
  Level   AS BIT * 4
END TYPE
DIM r AS StatusReg
r.Mode = 5
r.Level = 12
PRINT r.Mode
PRINT r.Level
