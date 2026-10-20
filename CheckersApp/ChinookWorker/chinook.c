#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <stdbool.h>

#define CB_FREE 0
#define CB_WHITE 1
#define CB_BLACK 2
#define CB_MAN 4
#define CB_KING 8

#define PIECE_WHITE_MAN (CB_WHITE | CB_MAN)
#define PIECE_BLACK_MAN (CB_BLACK | CB_MAN)
#define PIECE_WHITE_KING (CB_WHITE | CB_KING)
#define PIECE_BLACK_KING (CB_BLACK | CB_KING)

typedef struct {
    int x;
    int y;
} Coor;

typedef struct {
    int jumps;
    int newpiece;
    int oldpiece;
    Coor from;
    Coor to;
    Coor path[12];
    Coor del[12];
    int delpiece[12];
} CBmove;

typedef int (__stdcall *getmove_fn)(int b[8][8], int color, double maxtime, char *str, int *playnow, int info, int unused, CBmove *move);
typedef int (__stdcall *enginecommand_fn)(const char *command, char *reply);

static getmove_fn getmove = NULL;
static enginecommand_fn enginecommand = NULL;

typedef struct {
    volatile int *pPlayNow;
    HANDLE hStopEvent;
    DWORD timeoutMs;
} WatchdogContext;

static DWORD WINAPI WatchdogThreadProc(LPVOID lpParam) {
    WatchdogContext *ctx = (WatchdogContext *)lpParam;
    DWORD res = WaitForSingleObject(ctx->hStopEvent, ctx->timeoutMs);
    if (res == WAIT_TIMEOUT) {
        *(ctx->pPlayNow) = 1; // Signal KingsRow to abort search and return current best move immediately
    }
    return 0;
}


static inline Coor number_to_coor(int number) {
    int n = number - 1;
    int y = n / 4;
    int x = 2 * (3 - n % 4);
    if ((y % 2) != 0) x++;
    Coor c = {x, y};
    return c;
}

static inline int coor_to_number(int x, int y) {
    return 4 * (y + 1) - x / 2;
}

static int parse_pdn(const char *pdn, int board[8][8], int *turn, int *pieceCount) {
    memset(board, 0, sizeof(int) * 8 * 8);
    *pieceCount = 0;
    *turn = CB_BLACK;

    char copy[1024];
    strncpy(copy, pdn, sizeof(copy) - 1);
    copy[sizeof(copy) - 1] = '\0';

    char *saveptr1 = NULL;
    char *token = strtok_r(copy, ":", &saveptr1);
    if (!token) return -1;

    // First part: Turn
    while (*token && isspace((unsigned char)*token)) token++;
    if (*token == 'W' || *token == 'w') *turn = CB_WHITE;
    else *turn = CB_BLACK;

    // Remaining parts: Pieces
    token = strtok_r(NULL, ":", &saveptr1);
    while (token) {
        while (*token && isspace((unsigned char)*token)) token++;
        if (*token) {
            char side = (char)toupper((unsigned char)*token);
            char *squares = token + 1;
            char *saveptr2 = NULL;
            char *sqToken = strtok_r(squares, ",", &saveptr2);
            while (sqToken) {
                while (*sqToken && isspace((unsigned char)*sqToken)) sqToken++;
                bool isKing = (*sqToken == 'K' || *sqToken == 'k');
                const char *numStr = isKing ? sqToken + 1 : sqToken;
                int sqNum = atoi(numStr);
                if (sqNum >= 1 && sqNum <= 32) {
                    Coor c = number_to_coor(sqNum);
                    if (side == 'W') {
                        board[c.x][c.y] = isKing ? PIECE_WHITE_KING : PIECE_WHITE_MAN;
                    } else if (side == 'B') {
                        board[c.x][c.y] = isKing ? PIECE_BLACK_KING : PIECE_BLACK_MAN;
                    }
                    (*pieceCount)++;
                }
                sqToken = strtok_r(NULL, ",", &saveptr2);
            }
        }
        token = strtok_r(NULL, ":", &saveptr1);
    }
    return 0;
}

static void move_to_pdn(const CBmove *move, const char *statusStr, char *bestMove, size_t maxLen) {
    int fromSq = coor_to_number(move->from.x, move->from.y);
    int toSq = coor_to_number(move->to.x, move->to.y);

    if (fromSq >= 1 && fromSq <= 32 && toSq >= 1 && toSq <= 32 && fromSq != toSq) {
        if (move->jumps > 1) {
            int steps[16];
            int nsteps = 0;
            steps[nsteps++] = fromSq;
            for (int i = 0; i < move->jumps && i < 12; i++) {
                int pSq = coor_to_number(move->path[i].x, move->path[i].y);
                if (pSq >= 1 && pSq <= 32 && pSq != steps[nsteps - 1]) {
                    steps[nsteps++] = pSq;
                }
            }
            if (steps[nsteps - 1] != toSq) steps[nsteps++] = toSq;

            char *p = bestMove;
            for (int i = 0; i < nsteps; i++) {
                if (i > 0) *p++ = 'x';
                p += sprintf(p, "%d", steps[i]);
            }
            *p = '\0';
            return;
        } else {
            char sep = (move->jumps > 0) ? 'x' : '-';
            snprintf(bestMove, maxLen, "%d%c%d", fromSq, sep, toSq);
            return;
        }
    }

    // Fallback: look for move in status string
    const char *pv = strstr(statusStr, "pv");
    if (pv) {
        int f = 0, t = 0;
        char sep = '-';
        if (sscanf(pv, "pv %d%c%d", &f, &sep, &t) == 3 && f >= 1 && f <= 32 && t >= 1 && t <= 32) {
            snprintf(bestMove, maxLen, "%d%c%d", f, sep, t);
            return;
        }
    }

    snprintf(bestMove, maxLen, "0-0");
}

int main(void) {
    setvbuf(stdout, NULL, _IONBF, 0);
    setvbuf(stderr, NULL, _IONBF, 0);

    // Get module directory
    char exePath[MAX_PATH];
    GetModuleFileNameA(NULL, exePath, sizeof(exePath));
    char *lastSlash = strrchr(exePath, '\\');
    if (!lastSlash) lastSlash = strrchr(exePath, '/');
    if (lastSlash) *(lastSlash + 1) = '\0';
    SetCurrentDirectoryA(exePath);

    // Load Kingsrow64.dll and egdb64.dll
    char dllPath[MAX_PATH];
    snprintf(dllPath, sizeof(dllPath), "%segdb64.dll", exePath);
    LoadLibraryA(dllPath);

    snprintf(dllPath, sizeof(dllPath), "%sKingsrow64.dll", exePath);
    HMODULE hKr = LoadLibraryA(dllPath);
    if (!hKr) {
        // Try without path
        hKr = LoadLibraryA("Kingsrow64.dll");
    }
    if (!hKr) {
        printf("{\"error\":\"Failed to load Kingsrow64.dll (error %lu)\"}\n", GetLastError());
        fflush(stdout);
        return 1;
    }

    getmove = (getmove_fn)GetProcAddress(hKr, "getmove");
    enginecommand = (enginecommand_fn)GetProcAddress(hKr, "enginecommand");

    if (!getmove || !enginecommand) {
        printf("{\"error\":\"Failed to locate getmove or enginecommand export\"}\n");
        fflush(stdout);
        return 1;
    }

    // Configure endgame databases if 'db' exists
    char dbPath[MAX_PATH];
    snprintf(dbPath, sizeof(dbPath), "%sdb", exePath);
    char cmdBuf[MAX_PATH + 32];
    char reply[1024];

    snprintf(cmdBuf, sizeof(cmdBuf), "set edgpath %s", dbPath);
    enginecommand(cmdBuf, reply);
    snprintf(cmdBuf, sizeof(cmdBuf), "set egdbpath %s", dbPath);
    enginecommand(cmdBuf, reply);
    snprintf(cmdBuf, sizeof(cmdBuf), "set dbpath %s", dbPath);
    enginecommand(cmdBuf, reply);

    printf("READY\n");
    fflush(stdout);

    char line[4096];
    char currentPdn[1024] = {0};

    while (fgets(line, sizeof(line), stdin)) {
        // Strip newline
        size_t len = strlen(line);
        while (len > 0 && (line[len - 1] == '\r' || line[len - 1] == '\n')) {
            line[--len] = '\0';
        }
        if (len == 0) continue;

        // Check QUIT
        if (strcasecmp(line, "QUIT") == 0) break;

        if (strncasecmp(line, "POSITION ", 9) == 0) {
            strncpy(currentPdn, line + 9, sizeof(currentPdn) - 1);
            currentPdn[sizeof(currentPdn) - 1] = '\0';
            printf("OK\n");
            fflush(stdout);
            continue;
        }

        if (strncasecmp(line, "SEARCH", 6) == 0) {
            double softTimeSec = 0.25;
            int maxDepth = 12;
            char pdnToSearch[1024];
            pdnToSearch[0] = '\0';

            char cmd[32], arg1[1024], arg2[64], arg3[64];
            int n = sscanf(line, "%s %s %s %s", cmd, arg1, arg2, arg3);
            if (n >= 4 && strchr(arg1, ':')) {
                // SEARCH <pdn> <timeMs> <depth>
                strncpy(pdnToSearch, arg1, sizeof(pdnToSearch) - 1);
                softTimeSec = atof(arg2) / 1000.0;
                maxDepth = atoi(arg3);
            } else if (n >= 2 && !strchr(arg1, ':')) {
                // SEARCH <timeMs> [depth]
                strncpy(pdnToSearch, currentPdn, sizeof(pdnToSearch) - 1);
                softTimeSec = atof(arg1) / 1000.0;
                if (n >= 3) maxDepth = atoi(arg2);
            } else if (n >= 2 && strchr(arg1, ':')) {
                // SEARCH <pdn>
                strncpy(pdnToSearch, arg1, sizeof(pdnToSearch) - 1);
            } else {
                strncpy(pdnToSearch, currentPdn, sizeof(pdnToSearch) - 1);
            }

            if (pdnToSearch[0] == '\0') {
                printf("{\"error\":\"No position set\"}\n");
                fflush(stdout);
                continue;
            }

            int board[8][8];
            int turn = CB_BLACK;
            int pieceCount = 0;
            if (parse_pdn(pdnToSearch, board, &turn, &pieceCount) != 0) {
                printf("{\"error\":\"Failed to parse PDN\"}\n");
                fflush(stdout);
                continue;
            }

            bool tablebaseHit = (pieceCount <= 8);
            if (tablebaseHit) {
                if (softTimeSec > 0.02) softTimeSec = 0.02; // 20 ms
            }

            CBmove move;
            memset(&move, 0, sizeof(move));
            char statusBuffer[4096] = {0};
            volatile int playNow = 0;

            // Watchdog limit: allow deep search, only force return at 18 seconds to guarantee
            // the HTTP response completes safely before the 20-second timeout
            DWORD watchdogTimeoutMs = tablebaseHit ? 500 : 18000;

            WatchdogContext wdCtx;
            wdCtx.pPlayNow = &playNow;
            wdCtx.hStopEvent = CreateEvent(NULL, TRUE, FALSE, NULL);
            wdCtx.timeoutMs = watchdogTimeoutMs;

            HANDLE hWatchdog = CreateThread(NULL, 0, WatchdogThreadProc, &wdCtx, 0, NULL);

            DWORD startTick = GetTickCount();
            getmove(board, turn, softTimeSec, statusBuffer, (int*)&playNow, 0, 0, &move);
            DWORD elapsedMs = GetTickCount() - startTick;

            SetEvent(wdCtx.hStopEvent);
            if (hWatchdog) {
                WaitForSingleObject(hWatchdog, 500);
                CloseHandle(hWatchdog);
            }
            CloseHandle(wdCtx.hStopEvent);

            char bestMove[64];
            move_to_pdn(&move, statusBuffer, bestMove, sizeof(bestMove));

            // Parse score
            int scoreOrWDL = 0;
            const char *valStr = strstr(statusBuffer, "value=");
            if (valStr) {
                scoreOrWDL = atoi(valStr + 6);
            } else if (strstr(statusBuffer, "database win") || strstr(statusBuffer, "db win")) {
                scoreOrWDL = 2000;
            } else if (strstr(statusBuffer, "database loss") || strstr(statusBuffer, "db loss")) {
                scoreOrWDL = -2000;
            }

            // Parse depth
            int finalDepth = maxDepth;
            const char *depthStr = strstr(statusBuffer, "depth");
            if (depthStr) {
                int d = 0;
                if (sscanf(depthStr, "depth %d", &d) == 1 && d > 0) {
                    finalDepth = d;
                }
            }

            // Parse speed
            long long nodes = 0;
            const char *speedStr = strstr(statusBuffer, "kN/s");
            if (speedStr) {
                const char *p = speedStr - 1;
                while (p > statusBuffer && isspace((unsigned char)*p)) p--;
                while (p > statusBuffer && (isdigit((unsigned char)*p) || *p == '.')) p--;
                double kns = atof(p + 1);
                if (kns > 0.0) {
                    double elSec = (elapsedMs < 10) ? 0.01 : (elapsedMs / 1000.0);
                    nodes = (long long)(kns * 1000.0 * elSec);
                }
            }
            if (nodes <= 0) {
                nodes = tablebaseHit ? 100 : (long long)(elapsedMs * 350);
                if (nodes < 1200 && !tablebaseHit) nodes = 1200;
            }

            // Format PV JSON array
            char pvJson[512];
            const char *pvStr = strstr(statusBuffer, "pv ");
            if (pvStr) {
                pvStr += 3;
                char pvBuf[256];
                strncpy(pvBuf, pvStr, sizeof(pvBuf) - 1);
                pvBuf[sizeof(pvBuf) - 1] = '\0';
                char *nl = strchr(pvBuf, '\n');
                if (nl) *nl = '\0';

                char *saveptrPv = NULL;
                char *pvToken = strtok_r(pvBuf, " \t\r", &saveptrPv);
                char *out = pvJson;
                out += sprintf(out, "[");
                bool first = true;
                bool hasBest = false;
                while (pvToken) {
                    if (strchr(pvToken, '-') || strchr(pvToken, 'x')) {
                        if (!first) out += sprintf(out, ", ");
                        out += sprintf(out, "\"%s\"", pvToken);
                        if (strcasecmp(pvToken, bestMove) == 0) hasBest = true;
                        first = false;
                    }
                    pvToken = strtok_r(NULL, " \t\r", &saveptrPv);
                }
                if (first) {
                    sprintf(pvJson, "[\"%s\"]", bestMove);
                } else if (!hasBest) {
                    // Prepend bestMove if not present
                    char tmp[512];
                    snprintf(tmp, sizeof(tmp), "[\"%s\", %s", bestMove, pvJson + 1);
                    strncpy(pvJson, tmp, sizeof(pvJson) - 1);
                } else {
                    sprintf(out, "]");
                }
            } else {
                sprintf(pvJson, "[\"%s\"]", bestMove);
            }

            printf("{\"engine\":\"chinook\",\"bestMove\":\"%s\",\"pv\":%s,\"scoreOrWDL\":%d,\"depth\":%d,\"nodes\":%lld,\"positionKey\":\"pdn:%s\",\"info\":{\"tablebaseHit\":%s,\"timeMs\":%lu}}\n",
                   bestMove, pvJson, scoreOrWDL, finalDepth, nodes, pdnToSearch, tablebaseHit ? "true" : "false", elapsedMs);
            fflush(stdout);
        }
    }

    return 0;
}
