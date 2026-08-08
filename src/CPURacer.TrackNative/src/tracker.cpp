#define TRACKNATIVE_EXPORTS
#include "track_native.h"

#include <Windows.h>
#include <UIAutomation.h>
#include <atomic>
#include <string>
#include <vector>

#pragma comment(lib, "Ole32.lib")
#pragma comment(lib, "OleAut32.lib")
#pragma comment(lib, "UIAutomationCore.lib")

namespace {

constexpr wchar_t kMainClass[] = L"TaskManagerWindow";
constexpr wchar_t kChartClass[] = L"CvChartWindow";
constexpr int kMinChartW = 200;
constexpr int kMinChartH = 150;
constexpr UINT_PTR kRescanTimerId = 1;
constexpr UINT kRescanMs = 120;

TrackRoiCallback g_callback = nullptr;
void* g_user = nullptr;
std::atomic<bool> g_running{false};
std::atomic<int> g_followMode{TRACK_FOLLOW_EXTERNAL};

HWND g_msgWnd = nullptr;
HANDLE g_thread = nullptr;
DWORD g_threadId = 0;
HWINEVENTHOOK g_hookLoc = nullptr;
HWINEVENTHOOK g_hookFg = nullptr;

TrackRoiState g_last{};
bool g_hasLast = false;
HWND g_cpuLargeHwnd = nullptr;

std::wstring ClassName(HWND hwnd) {
    wchar_t buf[256]{};
    GetClassNameW(hwnd, buf, 256);
    return buf;
}

bool IsLive(HWND hwnd) {
    return hwnd && IsWindow(hwnd);
}

bool IsShown(HWND hwnd) {
    return IsLive(hwnd) && IsWindowVisible(hwnd);
}

struct ChartInfo {
    HWND hwnd = nullptr;
    RECT rc{};
    bool visible = false;
    long Area() const {
        return (rc.right - rc.left) * 1L * (rc.bottom - rc.top);
    }
};

bool IsMainGraph(const ChartInfo& c) {
    const int w = c.rc.right - c.rc.left;
    const int h = c.rc.bottom - c.rc.top;
    return w >= kMinChartW && h >= kMinChartH;
}

void CollectCharts(HWND parent, std::vector<ChartInfo>& out) {
    EnumChildWindows(
        parent,
        [](HWND hwnd, LPARAM lp) -> BOOL {
            auto* out = reinterpret_cast<std::vector<ChartInfo>*>(lp);
            if (ClassName(hwnd) == kChartClass) {
                RECT rc{};
                if (GetWindowRect(hwnd, &rc) && (rc.right - rc.left) > 0 && (rc.bottom - rc.top) > 0) {
                    bool dup = false;
                    for (const auto& e : *out) {
                        if (e.hwnd == hwnd) {
                            dup = true;
                            break;
                        }
                    }
                    if (!dup) {
                        out->push_back({hwnd, rc, IsWindowVisible(hwnd) != FALSE});
                    }
                }
            }
            CollectCharts(hwnd, *out);
            return TRUE;
        },
        reinterpret_cast<LPARAM>(&out));
}

HWND FindTaskmgrMain() {
    HWND found = nullptr;
    EnumWindows(
        [](HWND hwnd, LPARAM lp) -> BOOL {
            HWND* out = reinterpret_cast<HWND*>(lp);
            if (!IsShown(hwnd) || ClassName(hwnd) != kMainClass) {
                return TRUE;
            }
            DWORD pid = 0;
            GetWindowThreadProcessId(hwnd, &pid);
            bool ok = true;
            HANDLE proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
            if (proc) {
                wchar_t path[MAX_PATH]{};
                DWORD size = MAX_PATH;
                if (QueryFullProcessImageNameW(proc, 0, path, &size)) {
                    std::wstring p(path);
                    for (auto& ch : p) {
                        ch = towlower(ch);
                    }
                    ok = p.find(L"taskmgr.exe") != std::wstring::npos;
                }
                CloseHandle(proc);
            }
            if (!ok) {
                return TRUE;
            }
            RECT rc{};
            if (!GetWindowRect(hwnd, &rc) || (rc.right - rc.left) < 100 || (rc.bottom - rc.top) < 100) {
                return TRUE;
            }
            *out = hwnd;
            return FALSE;
        },
        reinterpret_cast<LPARAM>(&found));
    return found;
}

bool NameLooksLikeCpu(const std::wstring& name) {
    return !name.empty() && (name.rfind(L"CPU", 0) == 0 || name.rfind(L"Cpu", 0) == 0);
}

bool NameLooksLikeNonCpu(const std::wstring& name) {
    static const wchar_t* keys[] = {
        L"\x5185\x5b58", L"Memory", L"\x78c1\x76d8", L"Disk", L"GPU",
        L"\x4ee5\x592a\x7f51", L"Ethernet", L"Wi-Fi", L"WLAN", L"Bluetooth", L"\x84dd\x7259",
    };
    for (auto* k : keys) {
        if (name.find(k) != std::wstring::npos) {
            return true;
        }
    }
    return false;
}

// 1 = CPU, 0 = other, -1 = inconclusive (typical on Win11 XAML Taskmgr).
int TryUiaCpuPage(HWND mainHwnd) {
    IUIAutomation* automation = nullptr;
    if (FAILED(CoCreateInstance(CLSID_CUIAutomation, nullptr, CLSCTX_INPROC_SERVER, IID_IUIAutomation,
                                reinterpret_cast<void**>(&automation))) ||
        !automation) {
        return -1;
    }

    IUIAutomationElement* root = nullptr;
    if (FAILED(automation->ElementFromHandle(mainHwnd, &root)) || !root) {
        automation->Release();
        return -1;
    }

    VARIANT var{};
    var.vt = VT_BSTR;
    var.bstrVal = SysAllocString(L"CPU");
    IUIAutomationCondition* nameCond = nullptr;
    if (SUCCEEDED(automation->CreatePropertyCondition(UIA_NamePropertyId, var, &nameCond)) && nameCond) {
        IUIAutomationElementArray* exact = nullptr;
        if (SUCCEEDED(root->FindAll(TreeScope_Descendants, nameCond, &exact)) && exact) {
            int n = 0;
            exact->get_Length(&n);
            exact->Release();
            if (n > 0) {
                nameCond->Release();
                VariantClear(&var);
                root->Release();
                automation->Release();
                return 1;
            }
        }
        nameCond->Release();
    }
    VariantClear(&var);

    IUIAutomationCondition* trueCond = nullptr;
    automation->CreateTrueCondition(&trueCond);
    IUIAutomationElementArray* arr = nullptr;
    bool cpuSel = false, otherSel = false, sawName = false;
    if (trueCond && SUCCEEDED(root->FindAll(TreeScope_Descendants, trueCond, &arr)) && arr) {
        int count = 0;
        arr->get_Length(&count);
        for (int i = 0; i < count; ++i) {
            IUIAutomationElement* el = nullptr;
            if (FAILED(arr->GetElement(i, &el)) || !el) {
                continue;
            }
            BSTR nm = nullptr;
            el->get_CurrentName(&nm);
            if (nm && *nm) {
                sawName = true;
            }
            IUIAutomationSelectionItemPattern* sel = nullptr;
            if (SUCCEEDED(el->GetCurrentPatternAs(UIA_SelectionItemPatternId,
                                                  IID_IUIAutomationSelectionItemPattern,
                                                  reinterpret_cast<void**>(&sel))) &&
                sel) {
                BOOL selected = FALSE;
                sel->get_CurrentIsSelected(&selected);
                if (selected) {
                    std::wstring n = nm ? nm : L"";
                    if (NameLooksLikeCpu(n)) {
                        cpuSel = true;
                    } else if (NameLooksLikeNonCpu(n)) {
                        otherSel = true;
                    }
                }
                sel->Release();
            }
            if (nm) {
                SysFreeString(nm);
            }
            el->Release();
        }
        arr->Release();
    }
    if (trueCond) {
        trueCond->Release();
    }
    root->Release();
    automation->Release();

    if (cpuSel) {
        return 1;
    }
    if (otherSel) {
        return 0;
    }
    return -1;
}

bool AcceptCpuPage(HWND mainHwnd, HWND visibleLarge) {
    if (!visibleLarge) {
        return false;
    }
    const int uia = TryUiaCpuPage(mainHwnd);
    if (uia == 1) {
        g_cpuLargeHwnd = visibleLarge;
        return true;
    }
    if (uia == 0) {
        return false;
    }

    // Sticky HWND: first visible large chart binds as CPU; other page when that HWND hides.
    if (!IsLive(g_cpuLargeHwnd)) {
        g_cpuLargeHwnd = visibleLarge;
        return true;
    }
    if (visibleLarge == g_cpuLargeHwnd) {
        return true;
    }
    return IsWindowVisible(g_cpuLargeHwnd) != FALSE;
}

bool IsForegroundRelated(HWND mainHwnd, HWND chartHwnd) {
    HWND fg = GetForegroundWindow();
    for (HWND cur = fg; cur; cur = GetParent(cur)) {
        if (cur == mainHwnd || cur == chartHwnd) {
            return true;
        }
    }
    return false;
}

enum class FindChartResult {
    Found,
    Missing,
    NotCpuPage,
};

FindChartResult FindCpuChart(TrackRoiState& out) {
    ZeroMemory(&out, sizeof(out));
    out.follow_mode = g_followMode.load();

    HWND mainHwnd = FindTaskmgrMain();
    if (!mainHwnd) {
        return FindChartResult::Missing;
    }

    std::vector<ChartInfo> charts;
    CollectCharts(mainHwnd, charts);
    out.main_hwnd = reinterpret_cast<int64_t>(mainHwnd);
    out.chart_count = static_cast<int32_t>(charts.size());

    ChartInfo* bestVisible = nullptr;
    ChartInfo* bestAny = nullptr;
    long bestVisArea = 0;
    long bestAnyArea = 0;
    for (auto& c : charts) {
        if (!IsMainGraph(c)) {
            continue;
        }
        const long area = c.Area();
        if (area > bestAnyArea) {
            bestAnyArea = area;
            bestAny = &c;
        }
        if (c.visible && area > bestVisArea) {
            bestVisArea = area;
            bestVisible = &c;
        }
    }

    ChartInfo* pageCandidate = bestVisible ? bestVisible : bestAny;
    if (!pageCandidate) {
        return FindChartResult::Missing;
    }

    HWND pageHwnd = bestVisible ? bestVisible->hwnd : pageCandidate->hwnd;
    if (!AcceptCpuPage(mainHwnd, pageHwnd)) {
        out.is_cpu_page = 0;
        return FindChartResult::NotCpuPage;
    }
    out.is_cpu_page = 1;

    ChartInfo* best = bestVisible ? bestVisible : pageCandidate;
    if (IsShown(g_cpuLargeHwnd)) {
        for (auto& c : charts) {
            if (c.hwnd == g_cpuLargeHwnd && IsMainGraph(c)) {
                best = &c;
                break;
            }
        }
    }

    out.chart_hwnd = reinterpret_cast<int64_t>(best->hwnd);
    out.left = best->rc.left;
    out.top = best->rc.top;
    out.width = best->rc.right - best->rc.left;
    out.height = best->rc.bottom - best->rc.top;

    UINT dpi = GetDpiForWindow(best->hwnd);
    if (dpi == 0) {
        dpi = GetDpiForWindow(mainHwnd);
    }
    out.dpi = dpi ? dpi : 96;

    if (g_followMode.load() == TRACK_FOLLOW_CHILD) {
        out.should_show = IsShown(best->hwnd) ? 1 : 0;
    } else {
        out.should_show = IsForegroundRelated(mainHwnd, best->hwnd) ? 1 : 0;
    }
    return FindChartResult::Found;
}

bool StateEqual(const TrackRoiState& a, const TrackRoiState& b) {
    return a.chart_hwnd == b.chart_hwnd && a.main_hwnd == b.main_hwnd && a.left == b.left &&
           a.top == b.top && a.width == b.width && a.height == b.height && a.dpi == b.dpi &&
           a.chart_count == b.chart_count && a.should_show == b.should_show &&
           a.is_cpu_page == b.is_cpu_page && a.follow_mode == b.follow_mode;
}

void Emit(const TrackRoiState* state) {
    if (!g_callback) {
        return;
    }
    if (!state) {
        TrackRoiState empty{};
        empty.follow_mode = g_followMode.load();
        g_callback(&empty, g_user);
        g_hasLast = false;
        g_last = empty;
        return;
    }
    if (g_hasLast && StateEqual(g_last, *state)) {
        return;
    }
    g_last = *state;
    g_hasLast = true;
    g_callback(state, g_user);
}

void RefreshAndEmit() {
    TrackRoiState state{};
    const FindChartResult result = FindCpuChart(state);
    if (result == FindChartResult::Found) {
        Emit(&state);
        return;
    }

    // During live moves Taskmgr can briefly expose no chart candidate even though
    // the previously discovered HWND is still valid. C++ owns discovery continuity:
    // suppress that transient empty result instead of making the Overlay guess.
    if (result == FindChartResult::Missing && g_hasLast) {
        HWND lastChart = reinterpret_cast<HWND>(static_cast<intptr_t>(g_last.chart_hwnd));
        if (IsShown(lastChart)) {
            return;
        }
    }

    // NotCpuPage is definitive and must hide immediately; Missing reaches here
    // only when the previous HWND is no longer usable.
    Emit(nullptr);
}

void CALLBACK WinEventProc(HWINEVENTHOOK, DWORD event, HWND hwnd, LONG idObject, LONG, DWORD, DWORD) {
    if (!g_running.load()) {
        return;
    }
    if (event == EVENT_SYSTEM_FOREGROUND) {
        RefreshAndEmit();
        return;
    }
    if (idObject != OBJID_WINDOW || !g_hasLast) {
        if (!g_hasLast) {
            RefreshAndEmit();
        }
        return;
    }
    HWND chart = reinterpret_cast<HWND>(static_cast<intptr_t>(g_last.chart_hwnd));
    HWND main = reinterpret_cast<HWND>(static_cast<intptr_t>(g_last.main_hwnd));
    if ((hwnd == chart || hwnd == main) &&
        (event == EVENT_OBJECT_LOCATIONCHANGE || event == EVENT_OBJECT_DESTROY ||
         event == EVENT_OBJECT_HIDE || event == EVENT_OBJECT_SHOW)) {
        RefreshAndEmit();
    }
}

LRESULT CALLBACK MsgWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    if (msg == WM_TIMER && wParam == kRescanTimerId) {
        RefreshAndEmit();
        return 0;
    }
    if (msg == WM_DESTROY) {
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

DWORD WINAPI TrackThread(LPVOID) {
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    WNDCLASSW wc{};
    wc.lpfnWndProc = MsgWndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"CPURacer.TrackNative.Msg";
    RegisterClassW(&wc);
    g_msgWnd = CreateWindowExW(0, wc.lpszClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr,
                               wc.hInstance, nullptr);

    g_hookLoc = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, nullptr,
                                WinEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    g_hookFg = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nullptr, WinEventProc,
                               0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    SetTimer(g_msgWnd, kRescanTimerId, kRescanMs, nullptr);
    RefreshAndEmit();

    MSG msg;
    while (g_running.load() && GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (g_msgWnd) {
        KillTimer(g_msgWnd, kRescanTimerId);
        DestroyWindow(g_msgWnd);
        g_msgWnd = nullptr;
    }
    if (g_hookLoc) {
        UnhookWinEvent(g_hookLoc);
        g_hookLoc = nullptr;
    }
    if (g_hookFg) {
        UnhookWinEvent(g_hookFg);
        g_hookFg = nullptr;
    }
    CoUninitialize();
    return 0;
}

} // namespace

extern "C" {

TRACK_API void __stdcall Track_SetFollowMode(int mode) {
    g_followMode.store(mode == TRACK_FOLLOW_CHILD ? TRACK_FOLLOW_CHILD : TRACK_FOLLOW_EXTERNAL);
    if (g_msgWnd) {
        PostMessageW(g_msgWnd, WM_TIMER, kRescanTimerId, 0);
    }
}

TRACK_API int __stdcall Track_Start(TrackRoiCallback callback, void* user_data) {
    if (g_running.exchange(true)) {
        return 0;
    }
    g_callback = callback;
    g_user = user_data;
    g_hasLast = false;
    g_cpuLargeHwnd = nullptr;
    g_thread = CreateThread(nullptr, 0, TrackThread, nullptr, 0, &g_threadId);
    return g_thread ? 0 : -1;
}

TRACK_API void __stdcall Track_Stop(void) {
    if (!g_running.exchange(false)) {
        return;
    }
    if (g_msgWnd) {
        PostMessageW(g_msgWnd, WM_QUIT, 0, 0);
    }
    if (g_thread) {
        WaitForSingleObject(g_thread, 5000);
        CloseHandle(g_thread);
        g_thread = nullptr;
    }
    g_callback = nullptr;
    g_user = nullptr;
    g_hasLast = false;
    g_cpuLargeHwnd = nullptr;
}

TRACK_API int __stdcall Track_GetState(TrackRoiState* out_state) {
    if (!out_state) {
        return -1;
    }
    TrackRoiState state{};
    if (FindCpuChart(state) != FindChartResult::Found) {
        ZeroMemory(out_state, sizeof(*out_state));
        out_state->follow_mode = g_followMode.load();
        return 1;
    }
    *out_state = state;
    return 0;
}

} // extern "C"
