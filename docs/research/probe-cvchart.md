## CvChartWindow follow-up

- Time: 2026-08-08 02:06:15
- Taskmgr PID: 18740
- Visible CvChartWindow count (all): 5

| # | HWND | Size | Position |
|---|---|---|---|
| 1 | 132668 | 729x381 | (631,269) |
| 2 | 132668 | 729x381 | (631,269) |
| 3 | 132668 | 729x381 | (631,269) |
| 4 | 132668 | 729x381 | (631,269) |
| 5 | 132668 | 729x381 | (631,269) |

### Chart #1 HWND=132668 729x381

| Method | OK | Non-black | File |
|---|---|---|---|
| BitBlt | True | 930/930 | capture-cvchart-1-bitblt.png |
- BitBlt analysis: no saturated color found
| PrintWindow | False |  | draw failed |
| Screen | True | 930/930 | *(screenshot removed — captured IDE UI, not chart)* |
- Screen capture analysis: dominant RGB(226,115,129) sat=0.66 samples=1
- line-scan (top-down) hits=0/40
- height sample: `-, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -, -`
- bottom-up fill hits=1/20 (reference lunar-lander style)

### Chart #2 HWND=132668 729x381

| Method | OK | Non-black | File |
|---|---|---|---|
| BitBlt | True | 930/930 | capture-cvchart-2-bitblt.png |
| PrintWindow | False |  | draw failed |
| Screen | True | 930/930 | *(screenshot removed — captured IDE UI, not chart)* |

### Conclusions

1. Win11 Taskmgr exposes real chart HWNDs named `CvChartWindow` (not ChartView).
2. Largest `CvChartWindow` is the primary performance graph candidate.
3. Capture method success is recorded in tables above.
