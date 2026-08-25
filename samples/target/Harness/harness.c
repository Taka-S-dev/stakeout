/* stakeout 検証用サンプルターゲットのハーネス。
   NativeLib を直接叩き、タスク名パターン（Task_<name>_Main）を持つスレッドを回す。

   usage: Harness.exe [name] [--crash-after SEC] [--overflow] [--slow]

   仕込みバグはビルド時に選ぶ（samples/target/build.ps1 -Bug BUG_NN）。
   ここで選ぶのは「いつ踏ませるか」だけである。 */
#include "../NativeLib/nativelib.h"

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static volatile LONG g_running = 1;

/* 1 なら Task_A_Main を毎回スリープさせる。
   条件付きブレークポイントの検証には、毎秒 27 万回呼ばれる関数では速すぎる */
static int g_slow = 0;

/* BUG_05 を踏ませるまでの秒数。0 なら踏ませない */
static int g_crash_after_sec = 0;

/* 高頻度で nl_update_state を呼ぶ。トレースポイントのスループット計測用 */
static DWORD WINAPI Task_A_Main(LPVOID arg)
{
    int ev = 0;
    (void)arg;
    while (InterlockedCompareExchange(&g_running, 1, 1)) {
        nl_update_state(ev++);
        if (g_slow) {
            Sleep(1);
        } else if ((ev & 0xFFF) == 0) {
            Sleep(1);
        }
    }
    return 0;
}

/* 想定内の書き込み。データブレークポイントの「期待される犯人」 */
static DWORD WINAPI Task_B_Main(LPVOID arg)
{
    (void)arg;
    while (InterlockedCompareExchange(&g_running, 1, 1)) {
        nl_bump_counter(1);
        Sleep(50);
    }
    return 0;
}

/* 想定外の書き込み。find-corruption が当てるべき犯人。
   BUG_03 のビルドでだけ実際に書き込む */
static DWORD WINAPI Task_C_Main(LPVOID arg)
{
    (void)arg;
    while (InterlockedCompareExchange(&g_running, 1, 1)) {
        Sleep(1000);
#ifdef BUG_03
        nl_stray_write(0x0BADF00Dl);
#endif
    }
    return 0;
}

/* 設定を読む。BUG_05 のビルドではここでアクセス違反になる */
static DWORD WINAPI Task_D_Main(LPVOID arg)
{
    int elapsed_ms = 0;
    (void)arg;

    /* 秒数指定でも、デバッガからの要求でも踏める。
       後者があると、検証を時間に頼らず決定的に書ける */
    while (InterlockedCompareExchange(&g_running, 1, 1)) {
        if (g_crash_after_sec > 0 && elapsed_ms >= g_crash_after_sec * 1000) {
            g_crash_requested = 1;
        }

        if (g_crash_requested) {
            printf("Task_D_Main: reading config\n");
            fflush(stdout);
            printf("Task_D_Main: config=%d\n", nl_read_config());
            fflush(stdout);
            g_crash_requested = 0;
        }

        Sleep(200);
        elapsed_ms += 200;
    }

    return 0;
}

int main(int argc, char **argv)
{
    HANDLE th[4];
    const char *name = "harness";
    unsigned int last_tick = 0;
    int i;
    int overflow = 0;

    for (i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--crash-after") == 0 && i + 1 < argc) {
            g_crash_after_sec = atoi(argv[++i]);
        } else if (strcmp(argv[i], "--overflow") == 0) {
            overflow = 1;
        } else if (strcmp(argv[i], "--slow") == 0) {
            g_slow = 1;
        } else if (argv[i][0] != '-') {
            name = argv[i];
        }
    }

    nl_init(name);

    printf("harness pid=%lu name=%s bug=%s\n",
           GetCurrentProcessId(), name, nl_active_bug());
    printf("&g_ctx=%p &g_shared.counter=%p\n",
           (void *)&g_ctx, (void *)&g_shared.counter);
    fflush(stdout);

    if (overflow) {
        /* BUG_01 のビルドでは g_ctx.inner と g_ctx.self を踏み潰す */
        nl_set_name("0123456789abcdef0123456789abcdef0123456789");
        printf("after overflow: inner.id=%d inner.flags=%08X\n",
               g_ctx.inner.id, g_ctx.inner.flags);
        fflush(stdout);
    }

    th[0] = CreateThread(NULL, 0, Task_A_Main, NULL, 0, NULL);
    th[1] = CreateThread(NULL, 0, Task_B_Main, NULL, 0, NULL);
    th[2] = CreateThread(NULL, 0, Task_C_Main, NULL, 0, NULL);
    th[3] = CreateThread(NULL, 0, Task_D_Main, NULL, 0, NULL);

    /* 通常はここで回り続ける。デバッガから止めて使う。
       g_running を 0 にすると（デバッガの式評価からでも）終了する */
    while (InterlockedCompareExchange(&g_running, 1, 1)) {
        Sleep(1000);
        printf("tick=%u state=%d counter=%ld\n",
               g_ctx.tick - last_tick, g_ctx.state, g_shared.counter);
        fflush(stdout);
        last_tick = g_ctx.tick;
    }

    WaitForMultipleObjects(4, th, TRUE, 5000);
    for (i = 0; i < 4; i++) {
        CloseHandle(th[i]);
    }

    return 0;
}
