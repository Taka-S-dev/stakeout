/* stakeout 検証用サンプルターゲット。修正しないこと。 */
#define NATIVELIB_EXPORTS
#include "nativelib.h"

#include <string.h>

Ctx    g_ctx;
Shared g_shared;
volatile long g_crash_requested;

/* 状態遷移表。正常系は 0 -> 1 -> 2 -> 3 -> 0 */
static const int k_next_state[4] = { 1, 2, 3, 0 };

void nl_init(const char *name)
{
    memset(&g_ctx, 0, sizeof(g_ctx));
    memset((void *)&g_shared, 0, sizeof(g_shared));

    g_ctx.state       = 0;
    g_ctx.inner.id    = 1;
    g_ctx.inner.flags = 0xA5A5A5A5u;
    g_ctx.self        = &g_ctx;
    g_shared.guard    = 0x5A5A5A5Al;

    nl_set_name(name);
}

void nl_set_name(const char *name)
{
    if (name == NULL) {
        return;
    }

#ifdef BUG_01
    /* 長さを検査していない。name が 32 バイトを超えると g_ctx.inner を踏み潰す */
    strcpy(g_ctx.name, name);
#else
    strncpy(g_ctx.name, name, sizeof(g_ctx.name) - 1);
    g_ctx.name[sizeof(g_ctx.name) - 1] = '\0';
#endif
}

void nl_update_state(int ev)
{
    int next;

    if (g_ctx.state < 0 || g_ctx.state > 3) {
        next = 0;
    } else {
        next = k_next_state[g_ctx.state];
    }

#ifdef BUG_04
    /* 3 の次は 0 のはずが、ときどき 7 に飛ぶ。
       呼び出し回数ではなく「状態 3 に到達した回数」で数える。
       引数の位相で数えると、一度飛んだ時点で位相がずれ、二度と再現しなくなる */
    if (g_ctx.state == 3) {
        static unsigned int s_cycles_at_3;
        if (++s_cycles_at_3 % 250 == 0) {
            next = 7;
        }
    }
#endif
    (void)ev;

    g_ctx.state = next;
    g_ctx.tick++;
}

void nl_bump_counter(long delta)
{
    g_shared.counter += delta;
}

void nl_stray_write(long value)
{
#ifdef BUG_03
    /* 添字を 1 つ間違えている。scratch は 4 要素なので、[4] は範囲外であり、
       構造体上その直後にある counter を踏み潰す。
       ソースを grep しても counter への書き込みには見えない */
    g_shared.scratch[4] = value;
#else
    g_shared.scratch[3] = value;
#endif
}

int nl_deref(const int *p)
{
    return *p;
}

int nl_read_config(void)
{
    static const int k_default_config = 42;

#ifdef BUG_05
    /* 設定を読み込んでいないのに参照する。アクセス違反になる */
    const int *config = NULL;
    return nl_deref(config);
#else
    return nl_deref(&k_default_config);
#endif
}

const char *nl_active_bug(void)
{
#if defined(BUG_01)
    return "BUG_01";
#elif defined(BUG_03)
    return "BUG_03";
#elif defined(BUG_04)
    return "BUG_04";
#elif defined(BUG_05)
    return "BUG_05";
#else
    return "none";
#endif
}
