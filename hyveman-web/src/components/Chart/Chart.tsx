/** ECharts wrapper: bounded canvas rendering, resize handling, dispose on
 *  unmount. Charts never render event/API content as HTML. Theme defaults
 *  (text/divider colors, font family, tooltip surface) are merged under the
 *  caller's option so axes, legends, split lines, and tooltips stay legible
 *  in dark mode — without a theme, ECharts paints gray-on-dark defaults that
 *  disappear. Caller-provided values always win. */
import { useEffect, useMemo, useRef } from 'react';
import { Box } from '@mui/material';
import { useTheme, type Theme } from '@mui/material/styles';
import * as echarts from 'echarts/core';
import { BarChart, HeatmapChart, LineChart } from 'echarts/charts';
import {
  DataZoomComponent,
  GridComponent,
  LegendComponent,
  MarkLineComponent,
  TooltipComponent,
  VisualMapComponent,
} from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';
import type { EChartsCoreOption } from 'echarts/core';

echarts.use([
  BarChart,
  HeatmapChart,
  LineChart,
  GridComponent,
  TooltipComponent,
  LegendComponent,
  MarkLineComponent,
  DataZoomComponent,
  VisualMapComponent,
  CanvasRenderer,
]);

export interface ChartProps {
  option: EChartsCoreOption;
  height?: number;
  ariaLabel: string;
}

export function Chart({ option, height = 280, ariaLabel }: ChartProps) {
  const theme = useTheme();
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);

  // Theme-aware defaults merged under the caller's option every time either
  // changes. Caller values win, so explicit colors / axis styles still
  // override the defaults.
  const merged = useMemo(() => withThemeDefaults(option, theme), [option, theme]);

  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const chart = echarts.init(el, undefined, { renderer: 'canvas' });
    chartRef.current = chart;
    const observer = new ResizeObserver(() => chart.resize());
    observer.observe(el);
    return () => {
      observer.disconnect();
      chart.dispose();
      chartRef.current = null;
    };
  }, []);

  useEffect(() => {
    chartRef.current?.setOption(merged, { notMerge: true });
  }, [merged]);

  return (
    <Box
      ref={containerRef}
      role="img"
      aria-label={ariaLabel}
      sx={{ width: '100%', height }}
    />
  );
}

/** Deep-merge two plain option objects: arrays and scalars from `override`
 *  replace; nested plain objects merge. Only used to layer theme defaults. */
function mergeDeep(base: unknown, override: unknown): unknown {
  if (override === undefined) return base;
  if (override === null || typeof override !== 'object' || Array.isArray(override)) return override;
  if (base === null || typeof base !== 'object' || Array.isArray(base)) return override;
  const b = base as Record<string, unknown>;
  const ov = override as Record<string, unknown>;
  const out: Record<string, unknown> = { ...b };
  for (const key of Object.keys(ov)) {
    out[key] = mergeDeep(b[key], ov[key]);
  }
  return out;
}

/** Layers theme palette colors and the app font family under the caller's
 *  option. ECharts defaults to dark-gray axis text and `#ccc` split lines,
 *  which are near-invisible on a dark Paper; this keeps them readable. */
function withThemeDefaults(option: EChartsCoreOption, theme: Theme): EChartsCoreOption {
  const text = theme.palette.text.primary;
  const label = theme.palette.text.secondary;
  const divider = theme.palette.divider;
  const paper = theme.palette.background.paper;
  const fontFamily = theme.typography.fontFamily;

  // `nameTextStyle` defaults to a faint gray that disappears on dark Paper;
  // pair the axis name color with its axis accent so dual-series charts can
  // visually bind each name to its series color.
  const axisCommon = {
    axisLabel: { color: label },
    axisLine: { lineStyle: { color: divider } },
    splitLine: { lineStyle: { color: divider } },
    nameTextStyle: { color: label },
  };

  // xAxis / yAxis may be a single axis object or an array of them; apply the
  // common styling to each, letting the caller's keys (e.g. axisLabel.rotate)
  // survive.
  const applyAxisDefaults = (axis: unknown): unknown => {
    if (axis == null) return axisCommon;
    const apply = (a: Record<string, unknown>) => ({
      ...axisCommon,
      ...a,
      axisLabel: { ...axisCommon.axisLabel, ...(a.axisLabel as object | undefined) },
      axisLine: { ...axisCommon.axisLine, ...(a.axisLine as object | undefined) },
      splitLine: { ...axisCommon.splitLine, ...(a.splitLine as object | undefined) },
    });
    if (Array.isArray(axis)) return axis.map((a) => apply(a as Record<string, unknown>));
    return apply(axis as Record<string, unknown>);
  };

  const base = {
    backgroundColor: 'transparent',
    textStyle: { color: text, fontFamily },
    legend: { textStyle: { color: label } },
    tooltip: { backgroundColor: paper, borderColor: divider, textStyle: { color: text, fontFamily } },
  };

  const merged = mergeDeep(base, option) as Record<string, unknown>;
  const opt = option as unknown as Record<string, unknown>;
  merged.xAxis = applyAxisDefaults(opt.xAxis);
  merged.yAxis = applyAxisDefaults(opt.yAxis);
  return merged as unknown as EChartsCoreOption;
}