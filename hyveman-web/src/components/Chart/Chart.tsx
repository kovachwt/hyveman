/** ECharts wrapper: bounded canvas rendering, resize handling, dispose on
 *  unmount. Charts never render event/API content as HTML. */
import { useEffect, useRef } from 'react';
import { Box } from '@mui/material';
import * as echarts from 'echarts/core';
import { BarChart, LineChart } from 'echarts/charts';
import {
  DataZoomComponent,
  GridComponent,
  LegendComponent,
  MarkLineComponent,
  TooltipComponent,
} from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';
import type { EChartsCoreOption } from 'echarts/core';

echarts.use([
  BarChart,
  LineChart,
  GridComponent,
  TooltipComponent,
  LegendComponent,
  MarkLineComponent,
  DataZoomComponent,
  CanvasRenderer,
]);

export interface ChartProps {
  option: EChartsCoreOption;
  height?: number;
  ariaLabel: string;
}

export function Chart({ option, height = 280, ariaLabel }: ChartProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);

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
    chartRef.current?.setOption(option, { notMerge: true });
  }, [option]);

  return (
    <Box
      ref={containerRef}
      role="img"
      aria-label={ariaLabel}
      sx={{ width: '100%', height }}
    />
  );
}
