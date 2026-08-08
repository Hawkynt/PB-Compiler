/* ==========================================================================
 * pbc_rt.h - the C runtime ABI behind `pbc --emit-c` (and the LLVM path).
 * ==========================================================================
 * The IR middle end leaves everything that is not computation - strings, I/O,
 * array storage - as calls to this small extern ABI, exactly as it does for
 * LLVM. A target port therefore needs a back end (a few hundred lines) plus an
 * implementation of these functions; the front end, the lowering and all the
 * optimization passes are shared.
 *
 * Observable behaviour follows PowerBASIC, not C: PRINT gives a numeric value
 * a leading sign slot and a trailing space, drops the leading zero of a pure
 * fraction, and strings print unpadded.
 * ========================================================================== */
#ifndef PBC_RT_H
#define PBC_RT_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* A string handle. The IR treats it as an opaque pointer, so its shape is
   entirely the runtime's business - here a length plus the bytes. */
typedef struct pb_str {
  int32_t len;
  char *data;
} pb_str;

/* --- strings ----------------------------------------------------------- */
void *rt_str_const(void *bytes, int32_t len);
void *rt_str_concat(void *a, void *b);
void *rt_str_dup(void *s);
int32_t rt_str_len(void *s);
int32_t rt_str_compare(void *a, void *b);
void *rt_str_left(void *s, int32_t n);
void *rt_str_right(void *s, int32_t n);
void *rt_str_mid(void *s, int32_t start, int32_t len);
void *rt_str_mid2(void *s, int32_t start);
void *rt_str_mid_assign(void *dst, int32_t start, int32_t len, void *src);
void *rt_str_ucase(void *s);
void *rt_str_lcase(void *s);
void *rt_str_ltrim(void *s);
void *rt_str_rtrim(void *s);
void *rt_str_space(int32_t n);
void *rt_str_string(int32_t n, int32_t ch);
void *rt_str_string_s(int32_t n, void *s);
void *rt_str_chr(int32_t code);
int32_t rt_str_asc(void *s);
void *rt_str_hex(int32_t v);
void *rt_str_oct(int32_t v);
int32_t rt_str_instr(void *hay, void *needle);
int32_t rt_str_instr_start(int32_t start, void *hay, void *needle);
double rt_str_val(void *s);
void rt_str_to_fixed(void *dst, int32_t n, void *src);
void *rt_str_from_fixed(void *src, int32_t n);

/* STR$ of each numeric width (the suffix is the IR's own naming) */
void *rt_str_from_i8(int8_t v);
void *rt_str_from_u8(uint8_t v);
void *rt_str_from_i16(int16_t v);
void *rt_str_from_u16(uint16_t v);
void *rt_str_from_i32(int32_t v);
void *rt_str_from_u32(uint32_t v);
void *rt_str_from_i64(int64_t v);
void *rt_str_from_single(float v);
void *rt_str_from_double(double v);
void *rt_str_from_ext(long double v);

/* MKx$ / CVx binary record encoders */
void *rt_str_mki(int16_t v);
void *rt_str_mkl(int32_t v);
void *rt_str_mkdwd(int32_t v);
void *rt_str_mks(float v);
void *rt_str_mkd(double v);
int16_t rt_str_cvi(void *s);
int32_t rt_str_cvl(void *s);
int32_t rt_str_cvdwd(void *s);
float rt_str_cvs(void *s);
double rt_str_cvd(void *s);

/* --- console output ---------------------------------------------------- */
void rt_print_str(void *bytes, int32_t len);
void rt_print_strvar(void *s);
void rt_print_nl(void);
void rt_print_i8(int8_t v);
void rt_print_u8(uint8_t v);
void rt_print_i16(int16_t v);
void rt_print_u16(uint16_t v);
void rt_print_i32(int32_t v);
void rt_print_u32(uint32_t v);
void rt_print_i64(int64_t v);
/* Both take a long double, and that is the IR's contract rather than an oversight: a float is
   handed to the formatter at the x87's own width whatever its declared type, and the NAME picks
   the significant-digit count. Declaring these at their nominal widths made the emitted C
   contradict its own extern - "conflicting types for rt_print_single" - and narrowing here would
   undo exactly the precision the lowering keeps. */
void rt_print_single(long double v);
void rt_print_double(long double v);
void rt_print_ext(long double v);
void rt_print_tab(int32_t column);
void rt_print_spc(int32_t count);
void rt_print_zone(void);

/* --- console input ------------------------------------------------------ */
void rt_input_prompt(void *bytes, int32_t len);
int8_t rt_input_i8(void);
uint8_t rt_input_u8(void);
int16_t rt_input_i16(void);
uint16_t rt_input_u16(void);
int32_t rt_input_i32(void);
uint32_t rt_input_u32(void);
int64_t rt_input_i64(void);
float rt_input_single(void);
double rt_input_double(void);
long double rt_input_ext(void);
void *rt_input_str(void);
void *rt_input_line(void);

/* --- memory / arrays --------------------------------------------------- */
void *rt_arr_alloc(int32_t count, int32_t elementSize);
void *rt_arr_alloc_ptr(int32_t count);
void *rt_arr_realloc(void *p, int32_t count, int32_t elementSize);
void *rt_arr_realloc_ptr(void *p, int32_t count);
void rt_arr_free(void *p);
void rt_mem_copy(void *dst, void *src, int32_t n);
int32_t rt_mem_compare(void *a, void *b, int32_t n);

void rt_unreachable(void);

/* The generated translation unit defines this; main() in the runtime calls it. */
void pb_main(void);

#ifdef __cplusplus
}
#endif
#endif /* PBC_RT_H */
