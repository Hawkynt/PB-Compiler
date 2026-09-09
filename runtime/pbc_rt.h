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
void *rt_str_concat_n(int32_t count, ...);
void *rt_str_append_var(void *target, void *source);
void *rt_str_append_lit(void *target, void *bytes, int32_t len);
void *rt_str_dup(void *s);
void rt_str_free(void *s);

/* O0289 allocation coalescing is a DOS heap optimization. Hosted runtimes do not expose that heap,
   so the region markers are harmless no-ops and the specialized producers are ABI aliases of the
   ordinary string operations. Keeping real symbols here lets both emitted C and LLVM share the same
   optimized IR without target-specific pass suppression. */
void rt_str_coalesce_begin(int16_t capacity);
void rt_str_coalesce_end(void);
void *rt_str_const_coalesced(void *bytes, int32_t len);
void *rt_str_left_coalesced(void *s, int32_t n);
void *rt_str_right_coalesced(void *s, int32_t n);
void *rt_str_mid_coalesced(void *s, int32_t start, int32_t len);
void *rt_str_left_borrow_coalesced(void *s, int32_t n);
void *rt_str_right_borrow_coalesced(void *s, int32_t n);
void *rt_str_mid_borrow_coalesced(void *s, int32_t start, int32_t len);
void *rt_str_space_coalesced(int32_t n);
void *rt_str_string_coalesced(int32_t n, int32_t ch);
void *rt_str_chr_coalesced(int32_t code);

int32_t rt_str_len(void *s);
int32_t rt_str_compare(void *a, void *b);
int32_t rt_str_compare_eq(void *a, void *b);
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
int32_t rt_str_char_at(void *s, int32_t index);
void *rt_str_hex(int32_t v);
void *rt_str_oct(int32_t v);
void *rt_str_bin(int32_t v);
double rt_rnd(void);
int32_t rt_rnd_range(int32_t lower, int32_t upper);
void *rt_str_repeat(int32_t n, void *src);
void *rt_str_asc_set(void *s, int16_t pos, int16_t code);
/* (minimum digits << 8) | bits-per-digit - the one word the DOS rt_radix reads */
void *rt_str_radix(int32_t v, int32_t packed);
int32_t rt_str_instr(void *hay, void *needle);
int32_t rt_str_instr_start(int32_t start, void *hay, void *needle);
double rt_str_val(void *s);
void rt_str_to_fixed(void *dst, int32_t n, void *src);
void rt_str_to_fixed_r(void *dst, int32_t n, void *src);
void *rt_str_from_fixed(void *src, int32_t n);
/* LSET/RSET into a DYNAMIC string: justified in place, within the length already there */
void rt_str_justify(void *target, void *value, int16_t right);

/* STR$ of each numeric width (the suffix is the IR's own naming) */
void *rt_str_from_i8(int8_t v);
void *rt_str_from_u8(uint8_t v);
void *rt_str_from_i16(int16_t v);
void *rt_str_from_u16(uint16_t v);
void *rt_str_from_i32(int32_t v);
void *rt_str_from_u32(uint32_t v);
void *rt_str_from_i64(int64_t v);
void *rt_str_from_single(long double v);
void *rt_str_from_double(long double v);
void *rt_str_from_ext(long double v);

/* MKx$ / CVx binary record encoders */
void *rt_str_mkbyt(int16_t v);
void *rt_str_mki(int16_t v);
void *rt_str_mkl(int32_t v);
void *rt_str_mkdwd(int32_t v);
void *rt_str_mks(float v);
void *rt_str_mkd(double v);
int16_t rt_str_cvi(void *s);
/* CVBYT and CVWRD read UNSIGNED cells - a WORD's 50000 is 50000, not -15536 - and the IR
   declares them u16, so the C signature has to say so too or the emitted prototype conflicts
   with this header and the translation unit will not build. */
uint16_t rt_str_cvbyt(void *s);
uint16_t rt_str_cvwrd(void *s);
int32_t rt_str_cvl(void *s);
int32_t rt_str_cvdwd(void *s);
float rt_str_cvs(void *s);
double rt_str_cvd(void *s);
long double rt_str_cve(void *s);

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
int16_t rt_csrlin(void);
int16_t rt_consin(void);
int16_t rt_consout(void);
void rt_defseg_reset(void);
void rt_print_comma(void);
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

/* sequential file I/O - INPUT / OUTPUT / APPEND; the file number comes first, as the lowering's
   rt_print_x -> rt_fprint_x naming rule requires */
void rt_file_open(int32_t n, void *name, int32_t mode, int32_t reclen);
void rt_file_close(int32_t n);
void rt_file_close_all(void);
int16_t rt_freefile(void);
int16_t rt_eof(int16_t n);
void rt_kill(void *name);
void rt_fprint_str(int32_t n, void *bytes, int32_t len);
void rt_fprint_strvar(int32_t n, void *s);
void rt_fprint_nl(int32_t n);
void rt_fprint_comma(int32_t n);
void rt_fprint_i8(int32_t n, int8_t v);
void rt_fprint_u8(int32_t n, uint8_t v);
void rt_fprint_i16(int32_t n, int16_t v);
void rt_fprint_u16(int32_t n, uint16_t v);
void rt_fprint_i32(int32_t n, int32_t v);
void rt_fprint_u32(int32_t n, uint32_t v);
void rt_fprint_i64(int32_t n, int64_t v);
void rt_fprint_single(int32_t n, long double v);
void rt_fprint_double(int32_t n, long double v);
void *rt_finput_line(int32_t n);
void rt_file_put(int32_t n, int32_t record, void *value, int32_t size);
void rt_file_get(int32_t n, int32_t record, void *value, int32_t size);
int32_t rt_file_length(int32_t n);
int32_t rt_file_pos(int32_t n);
void rt_file_seek(int32_t n, int32_t position);
void rt_fput_str(int32_t n, void *s);
void *rt_fget_str(int32_t n, int32_t count);

/* O0297 expression-local string views. The optimized IR carries a stable handle plus a 1-based
   start and byte length. These helpers borrow the handle: they never allocate, copy, or free it.
   Hosted C has a non-moving heap, but keeping the same ABI as DOS makes the IR target-neutral. */
static inline int32_t rt_str_len_borrow(void *s) {
  return s ? ((pb_str *)s)->len : 0;
}

static inline void pbc_rt_string_view(void *s, int32_t start, int32_t len,
    const unsigned char **data, int32_t *view_len) {
  static const unsigned char empty = 0;
  pb_str *x = s ? (pb_str *)s : (pb_str *)0;
  int32_t source_len = x ? x->len : 0;
  if (start < 1) start = 1;
  if (start > source_len || len <= 0) {
    *data = &empty;
    *view_len = 0;
    return;
  }
  if (len > source_len - start + 1)
    len = source_len - start + 1;
  *data = (const unsigned char *)x->data + start - 1;
  *view_len = len;
}

static inline int32_t rt_str_compare_view(void *a, int32_t a_start, int32_t a_len,
    void *b, int32_t b_start, int32_t b_len) {
  const unsigned char *x, *y;
  int32_t xn, yn, n, i;
  pbc_rt_string_view(a, a_start, a_len, &x, &xn);
  pbc_rt_string_view(b, b_start, b_len, &y, &yn);
  n = xn < yn ? xn : yn;
  for (i = 0; i < n; ++i)
    if (x[i] != y[i])
      return x[i] < y[i] ? -1 : 1;
  return xn == yn ? 0 : (xn < yn ? -1 : 1);
}

static inline int32_t rt_str_compare_eq_view(void *a, int32_t a_start, int32_t a_len,
    void *b, int32_t b_start, int32_t b_len) {
  const unsigned char *x, *y;
  int32_t xn, yn, i;
  pbc_rt_string_view(a, a_start, a_len, &x, &xn);
  pbc_rt_string_view(b, b_start, b_len, &y, &yn);
  if (xn != yn) return 1;
  for (i = 0; i < xn; ++i)
    if (x[i] != y[i]) return 1;
  return 0;
}

static inline void rt_print_strview(void *s, int32_t start, int32_t len) {
  const unsigned char *data;
  int32_t n;
  pbc_rt_string_view(s, start, len, &data, &n);
  rt_print_str((void *)data, n);
}

static inline void rt_fprint_strview(int32_t file, void *s, int32_t start, int32_t len) {
  const unsigned char *data;
  int32_t n;
  pbc_rt_string_view(s, start, len, &data, &n);
  rt_fprint_str(file, (void *)data, n);
}

/* --- memory / arrays --------------------------------------------------- */
/* Sizes are BYTES; the _ptr variants take element COUNTS because only this file knows how wide a
   target pointer is. See the definitions for the whole argument. */
void *rt_arr_alloc(int32_t bytes);
void *rt_arr_alloc_nz(int32_t bytes);
void *rt_arr_alloc_ptr(int32_t count);
void *rt_arr_realloc(void *p, int32_t oldBytes, int32_t newBytes);
void *rt_arr_realloc_ptr(void *p, int32_t oldCount, int32_t newCount);
void rt_arr_free(void *p, int32_t bytes);
void rt_arr_free_ptr(void *p, int32_t count);
void rt_mem_copy(void *dst, void *src, int32_t n);
int32_t rt_mem_compare(void *a, void *b, int32_t n);

void rt_error(int32_t code);
void rt_unreachable(void);

/* The generated translation unit defines this; main() in the runtime calls it. */
void pb_main(void);

#ifdef __cplusplus
}
#endif
#endif /* PBC_RT_H */