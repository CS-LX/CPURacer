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
constexpr int kMinSidebarCharts = 3;
constexpr UINT kRescanMsg = WM_APP + 41;
constexpr UINT_PTR kRescanTimerId = 1;

TrackRoiCallback g_callback = nullptr;
void* g_user = nullptr;
std::atomic<bool> g_running{false};
HWND g_msgWnd = nullptr;
HANDLE g_thread = nullptr;
DWORD g_threadId = 0;
HWINEVENTHOOK g_hookLoc = nullptr;
HWINEVENTHOOK g_hookFg = nullptr;
TrackRoiState g_last{};
bool g_hasLast = false;
// Win11 Taskmgr keeps one large CvChartWindow per resource and toggles visibility.
// UIA names are often empty (XAML islands), so we remember the CPU large-chart HWND.
HWND g_cpuLargeHwnd = nullptr;

std::wstring GetClass(HWND hwnd) {
    wchar_t buf[256]{};
    GetClassNameW(hwnd, buf, 256);
    return buf;
}

bool IsVisible(HWND hwnd) {
    return hwnd && IsWindow(hwnd) && IsWindowVisible(hwnd);
}

struct ChartInfo {
    HWND hwnd = nullptr;
    RECT rc{};
    bool visible = false;
};

bool IsSidebarSparkline(const ChartInfo& c) {
    const int w = c.rc.right - c.rc.left;
    const int h = c.rc.bottom - c.rc.top;
    return w >= 40 && w < 150 && h >= 20 && h < 120;
}

bool IsMainGraph(const ChartInfo& c) {
    const int w = c.rc.right - c.rc.left;
    const int h = c.rc.bottom - c.rc.top;
    return w >= kMinChartW && h >= kMinChartH;
}

void CollectCharts(HWND parent, std::vector<ChartInfo>& out) {
    // Walk immediate children and recurse. Deduplicate: some hosts report the same HWND
    // through multiple paths; EnumChildWindows descendant behavior varies by OS build.
    EnumChildWindows(
        parent,
        [](HWND hwnd, LPARAM lp) -> BOOL {
            auto* out = reinterpret_cast<std::vector<ChartInfo>*>(lp);
            if (GetClass(hwnd) == kChartClass) {
                RECT rc{};
                if (GetWindowRect(hwnd, &rc) && (rc.right - rc.left) > 0 && (rc.bottom - rc.top) > 0) {
                    bool exists = false;
                    for (const auto& existing : *out) {
                        if (existing.hwnd == hwnd) {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists) {
                        // Prefer IsWindowVisible; also accept if window is showing on screen
                        // (DirectUI/XAML sometimes reports odd visibility for painted charts).
                        const bool vis = IsWindowVisible(hwnd) != FALSE;
                        out->push_back({hwnd, rc, vis});
                    }
                }
            }
            CollectCharts(hwnd, *out);
            return TRUE;
        },
        reinterpret_cast<LPARAM>(&out));
}

bool NameLooksLikeCpu(const std::wstring& name) {
    if (name.empty()) {
        return false;
    }
    // Win11 zh-CN sidebar still uses "CPU" prefix; also accept English.
    return name.rfind(L"CPU", 0) == 0 || name.rfind(L"Cpu", 0) == 0;
}

bool NameLooksLikeNonCpuPerf(const std::wstring& name) {
    // Avoid non-ASCII source encoding issues: use hex escapes for zh labels.
    static const wchar_t* keys[] = {
        L"\x5185\x5b58", // 内存
        L"Memory",
        L"\x78c1\x76d8", // 磁盘
        L"Disk",
        L"GPU",
        L"\x4ee5\x592a\x7f51", // 以太网
        L"Ethernet",
        L"Wi-Fi",
        L"WLAN",
        L"Bluetooth",
        L"\x84dd\x7259", // 蓝牙
    };
    for (size_t i = 0; i < sizeof(keys) / sizeof(keys[0]); ++i) {
        if (name.find(keys[i]) != std::wstring::npos) {
            return true;
        }
    }
    return false;
}

bool HasExactCpuTitle(HWND mainHwnd, IUIAutomation* automation, IUIAutomationElement* root) {
    VARIANT var{};
    var.vt = VT_BSTR;
    var.bstrVal = SysAllocString(L"CPU");
    IUIAutomationCondition* nameCond = nullptr;
    HRESULT hr = automation->CreatePropertyCondition(UIA_NamePropertyId, var, &nameCond);
    VariantClear(&var);
    if (FAILED(hr) || !nameCond) {
        return false;
    }

    IUIAutomationElementArray* arr = nullptr;
    hr = root->FindAll(TreeScope_Descendants, nameCond, &arr);
    nameCond->Release();
    bool found = false;
    if (SUCCEEDED(hr) && arr) {
        int count = 0;
        arr->get_Length(&count);
        found = count > 0;
        arr->Release();
    }
    return found;
}

// Returns: 1 = CPU, 0 = other perf page, -1 = UIA inconclusive (XAML island / empty names).
int TryUiaCpuPage(HWND mainHwnd) {
    if (!mainHwnd) {
        return -1;
    }

    IUIAutomation* automation = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_CUIAutomation, nullptr, CLSCTX_INPROC_SERVER,
                                  IID_IUIAutomation, reinterpret_cast<void**>(&automation));
    if (FAILED(hr) || !automation) {
        return -1;
    }

    IUIAutomationElement* root = nullptr;
    hr = automation->ElementFromHandle(mainHwnd, &root);
    if (FAILED(hr) || !root) {
        automation->Release();
        return -1;
    }

    if (HasExactCpuTitle(mainHwnd, automation, root)) {
        root->Release();
        automation->Release();
        return 1;
    }

    IUIAutomationCondition* trueCond = nullptr;
    automation->CreateTrueCondition(&trueCond);

    IUIAutomationElementArray* arr = nullptr;
    hr = root->FindAll(TreeScope_Descendants, trueCond, &arr);
    bool cpuSelected = false;
    bool otherSelected = false;
    bool sawAnyName = false;

    if (SUCCEEDED(hr) && arr) {
        int count = 0;
        arr->get_Length(&count);
        for (int i = 0; i < count; ++i) {
            IUIAutomationElement* el = nullptr;
            if (FAILED(arr->GetElement(i, &el)) || !el) {
                continue;
            }

            BSTR anyName = nullptr;
            el->get_CurrentName(&anyName);
            if (anyName && *anyName) {
                sawAnyName = true;
            }
            if (anyName) {
                SysFreeString(anyName);
            }

            IUIAutomationSelectionItemPattern* sel = nullptr;
            hr = el->GetCurrentPatternAs(UIA_SelectionItemPatternId,
                                         IID_IUIAutomationSelectionItemPattern,
                                         reinterpret_cast<void**>(&sel));
            if (SUCCEEDED(hr) && sel) {
                BOOL isSelected = FALSE;
                sel->get_CurrentIsSelected(&isSelected);
                if (isSelected) {
                    BSTR name = nullptr;
                    el->get_CurrentName(&name);
                    std::wstring n = name ? name : L"";
                    if (name) {
                        SysFreeString(name);
                    }
                    if (NameLooksLikeCpu(n)) {
                        cpuSelected = true;
                    } else if (NameLooksLikeNonCpuPerf(n)) {
                        otherSelected = true;
                    }
                }
                sel->Release();
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

    if (cpuSelected) {
        return 1;
    }
    if (otherSelected) {
        return 0;
    }
    // Modern Taskmgr: XAML island → almost no useful names/selection. Treat as inconclusive.
    if (!sawAnyName) {
        return -1;
    }
    return -1;
}

bool IsCpuPageSelected(HWND mainHwnd, HWND visibleLargeHwnd) {
    if (!visibleLargeHwnd) {
        return false;
    }

    const int uia = TryUiaCpuPage(mainHwnd);
    if (uia == 1) {
        g_cpuLargeHwnd = visibleLargeHwnd;
        return true;
    }
    if (uia == 0) {
        return false;
    }

    // UIA inconclusive (Win11 XAML Taskmgr): TaskmgrPlayer-style — accept the current
    // visible large chart. Sticky HWND still used so switching Memory/GPU (different
    // visible large HWND, CPU hwnd hidden) can hide the overlay.
    if (!g_cpuLargeHwnd || !IsWindow(g_cpuLargeHwnd)) {
        g_cpuLargeHwnd = visibleLargeHwnd;
        return true;
    }
    if (visibleLargeHwnd == g_cpuLargeHwnd) {
        return true;
    }
    if (!IsWindowVisible(g_cpuLargeHwnd)) {
        return false;
    }
    return true;
}

bool IsForegroundRelated(HWND mainHwnd, HWND chartHwnd) {
    HWND fg = GetForegroundWindow();
    if (!fg) {
        return false;
    }
    for (HWND cur = fg; cur; cur = GetParent(cur)) {
        if (cur == mainHwnd || cur == chartHwnd) {
            return true;
        }
    }
    return false;
}

bool FindCpuChart(TrackRoiState& out) {
    ZeroMemory(&out, sizeof(out));

    HWND mainHwnd = nullptr;
    EnumWindows(
        [](HWND hwnd, LPARAM lp) -> BOOL {
            HWND* mainHwnd = reinterpret_cast<HWND*>(lp);
            if (!IsVisible(hwnd)) {
                return TRUE;
            }
            if (GetClass(hwnd) != kMainClass) {
                return TRUE;
            }
            DWORD pid = 0;
            GetWindowThreadProcessId(hwnd, &pid);
            HANDLE proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
            bool isTaskmgr = true;
            if (proc) {
                wchar_t path[MAX_PATH]{};
                DWORD size = MAX_PATH;
                if (QueryFullProcessImageNameW(proc, 0, path, &size)) {
                    std::wstring p(path);
                    for (auto& ch : p) {
                        ch = towlower(ch);
                    }
                    isTaskmgr = p.find(L"taskmgr.exe") != std::wstring::npos;
                }
                CloseHandle(proc);
            }
            if (!isTaskmgr) {
                return TRUE;
            }
            RECT rc{};
            if (!GetWindowRect(hwnd, &rc) || (rc.right - rc.left) < 100 || (rc.bottom - rc.top) < 100) {
                return TRUE;
            }
            *mainHwnd = hwnd;
            return FALSE; // stop on first
        },
        reinterpret_cast<LPARAM>(&mainHwnd));

    if (!mainHwnd) {
        return false;
    }

    std::vector<ChartInfo> charts;
    CollectCharts(mainHwnd, charts);
    out.main_hwnd = reinterpret_cast<int64_t>(mainHwnd);
    out.chart_count = static_cast<int32_t>(charts.size());

    // Same core idea as TaskmgrPlayer: largest CvChartWindow by area.
    // Prefer visible; if none report visible (DirectUI quirk), fall back to absolute largest.
    ChartInfo* bestVisibleLarge = nullptr;
    ChartInfo* bestAnyLarge = nullptr;
    long bestVisArea = 0;
    long bestAnyArea = 0;
    for (auto& c : charts) {
        if (!IsMainGraph(c)) {
            continue;
        }
        const long area = (c.rc.right - c.rc.left) * 1L * (c.rc.bottom - c.rc.top);
        if (area > bestAnyArea) {
            bestAnyArea = area;
            bestAnyLarge = &c;
        }
        if (c.visible && area > bestVisArea) {
            bestVisArea = area;
            bestVisibleLarge = &c;
        }
    }

    out.chart_count = static_cast<int32_t>(charts.size());

    ChartInfo* candidate = bestVisibleLarge ? bestVisibleLarge : bestAnyLarge;
    if (!candidate) {
        return false;
    }

    // Bind / filter CPU page only from *visible* large charts. Binding a hidden twin
    // (slightly larger area on Win11) then rejecting the real visible chart caused
    // permanent "no CPU chart".
    HWND pageHwnd = bestVisibleLarge ? bestVisibleLarge->hwnd : candidate->hwnd;
    const bool cpuPage = IsCpuPageSelected(mainHwnd, pageHwnd);
    out.is_cpu_page = cpuPage ? 1 : 0;
    if (!cpuPage) {
        return false;
    }

    ChartInfo* best = candidate;
    if (g_cpuLargeHwnd && IsWindow(g_cpuLargeHwnd) && IsWindowVisible(g_cpuLargeHwnd)) {
        for (auto& c : charts) {
            if (c.hwnd == g_cpuLargeHwnd && IsMainGraph(c)) {
                best = &c;
                break;
            }
        }
    } else if (bestVisibleLarge) {
        best = bestVisibleLarge;
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
    if (dpi == 0) {
        dpi = 96;
    }
    out.dpi = dpi;
    out.should_show = IsForegroundRelated(mainHwnd, best->hwnd) ? 1 : 0;
    return true;
}

bool StateEqual(const TrackRoiState& a, const TrackRoiState& b) {
    return a.chart_hwnd == b.chart_hwnd && a.main_hwnd == b.main_hwnd && a.left == b.left &&
           a.top == b.top && a.width == b.width && a.height == b.height && a.dpi == b.dpi &&
           a.chart_count == b.chart_count && a.should_show == b.should_show &&
           a.is_cpu_page == b.is_cpu_page;
}

void Emit(const TrackRoiState* state) {
    if (!g_callback) {
        return;
    }
    if (state == nullptr) {
        TrackRoiState empty{};
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
    if (!FindCpuChart(state)) {
        Emit(nullptr);
        return;
    }
    Emit(&state);
}

void CALLBACK WinEventProc(HWINEVENTHOOK, DWORD event, HWND hwnd, LONG idObject, LONG, DWORD,
                           DWORD) {
    if (!g_running.load()) {
        return;
    }
    if (event == EVENT_SYSTEM_FOREGROUND) {
        RefreshAndEmit();
        return;
    }
    if (idObject != OBJID_WINDOW) {
        return;
    }
    if (!g_hasLast) {
        RefreshAndEmit();
        return;
    }
    HWND chart = reinterpret_cast<HWND>(static_cast<intptr_t>(g_last.chart_hwnd));
    HWND main = reinterpret_cast<HWND>(static_cast<intptr_t>(g_last.main_hwnd));
    if (hwnd == chart || hwnd == main) {
        if (event == EVENT_OBJECT_LOCATIONCHANGE || event == EVENT_OBJECT_DESTROY ||
            event == EVENT_OBJECT_HIDE || event == EVENT_OBJECT_SHOW) {
            RefreshAndEmit();
        }
    }
}

LRESULT CALLBACK MsgWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_TIMER:
        if (wParam == kRescanTimerId) {
            RefreshAndEmit();
        }
        return 0;
    case kRescanMsg:
        RefreshAndEmit();
        return 0;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    default:
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}

DWORD WINAPI TrackThread(LPVOID) {
    // STA: UIA is more reliable; also required for some accessibility providers.
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

    SetTimer(g_msgWnd, kRescanTimerId, 120, nullptr);
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
    if (!FindCpuChart(state)) {
        ZeroMemory(out_state, sizeof(*out_state));
        return 1;
    }
    *out_state = state;
    return 0;
}

} // extern "C"
