/* stakeout 検証用サンプルターゲット。
   ここに書かれた不自然なコードは意図的なものである。修正しないこと。

   仕込みバグは BUG_NN マクロで切り替える（design.md §18.1）。
   ビルド例: samples/target/build.ps1 -Bug BUG_04 */
#ifndef NATIVELIB_H
#define NATIVELIB_H

#ifdef NATIVELIB_EXPORTS
#define NL_API __declspec(dllexport)
#else
#define NL_API __declspec(dllimport)
#endif

#include <stddef.h>

typedef struct Inner {
    int          id;
    unsigned int flags;
} Inner;

/* 調査対象の主構造体。stakeout dump / eval のテストに使う */
typedef struct Ctx {
    int            state;      /* 状態遷移。0 -> 1 -> 2 -> 3 -> 0 を繰り返す */
    unsigned int   tick;       /* update_state の呼び出し回数 */
    char           name[32];   /* 固定長バッファ。BUG_01 の標的 */
    Inner          inner;
    struct Ctx    *self;       /* ポインタ展開のテスト用（自分を指す） */
} Ctx;

/* 複数スレッドから触られる共有領域。データブレークポイントのテストに使う。
   scratch の直後に counter を置いてあるのは意図的である（BUG_03 の標的） */
typedef struct Shared {
    long          scratch[4];  /* タスク C の作業領域 */
    volatile long counter;     /* 共有カウンタ。scratch の範囲外書き込みが届く */
    long          guard;       /* 番兵 */
} Shared;

NL_API extern Ctx    g_ctx;
NL_API extern Shared g_shared;

/* 0 以外にすると Task_D_Main が設定読み込みを試みる。
   デバッガの式評価から書き換えて、任意のタイミングでバグを踏ませるためにある */
NL_API extern volatile long g_crash_requested;

NL_API void nl_init(const char *name);

/* 高頻度で呼ばれる。トレースポイントのスループット計測に使う。
   BUG_04 では状態が 3 から 7 に飛ぶ */
NL_API void nl_update_state(int ev);

/* g_ctx.name への書き込み。BUG_01 では長さを検査しない */
NL_API void nl_set_name(const char *name);

/* g_shared.counter への「想定内」の書き込み */
NL_API void nl_bump_counter(long delta);

/* g_shared.counter への「想定外」の書き込み。find-corruption の正解 */
NL_API void nl_stray_write(long value);

NL_API int  nl_deref(const int *p);

/* BUG_05 ではヌルポインタを渡してアクセス違反を起こす */
NL_API int  nl_read_config(void);

/* 仕込まれているバグの名前を返す。何も仕込まれていなければ "none" */
NL_API const char *nl_active_bug(void);

#endif /* NATIVELIB_H */
