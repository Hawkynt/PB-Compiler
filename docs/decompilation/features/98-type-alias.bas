' @title: Type alias
' @desc:  TYPE Name AS type names an existing type; it resolves away at bind time, so the decompilation shows only the underlying type.
TYPE Handle AS DWORD
TYPE FileHandle AS Handle
DIM h AS FileHandle
h = 42
PRINT h
