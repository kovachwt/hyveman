/**
 * Dense data table with optional windowed virtualization for large result
 * sets (FRONTEND.md §8.3/§12). Preserves the most important columns first;
 * the scroll container owns overflow so headers stay sticky.
 */
import { useRef } from 'react';
import { Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Typography } from '@mui/material';
import { useVirtualizer } from '@tanstack/react-virtual';
import type { ReactNode } from 'react';

export interface Column<T> {
  id: string;
  label: ReactNode;
  align?: 'left' | 'right' | 'center';
  /** Fixed width for table-layout; narrow tables use flexible layout. */
  width?: number | string;
  render: (row: T) => ReactNode;
  /** Preserve this column on small screens (media query hides others). */
  always?: boolean;
}

export interface DataTableProps<T> {
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T) => string | number;
  dense?: boolean;
  /** Window large result sets (row heights must be uniform). */
  virtualize?: boolean;
  maxHeight?: number | string;
  rowHeight?: number;
  emptyText?: string;
  getRowProps?: (row: T) => React.HTMLAttributes<HTMLTableRowElement>;
  'aria-label'?: string;
  /** Drop the container border so a wrapping Paper can own the edge instead
   *  of producing a nested double border. Default keeps the border. */
  disableBorder?: boolean;
}

export function DataTable<T>({
  columns,
  rows,
  rowKey,
  dense = true,
  virtualize = false,
  maxHeight = 560,
  rowHeight = dense ? 38 : 52,
  emptyText = 'No rows to display.',
  getRowProps,
  disableBorder = false,
  'aria-label': ariaLabel,
}: DataTableProps<T>) {
  const parentRef = useRef<HTMLDivElement>(null);
  const rowVirtualizer = useVirtualizer({
    count: virtualize ? rows.length : 0,
    getScrollElement: () => parentRef.current,
    estimateSize: () => rowHeight,
    overscan: 8,
  });

  const useFixedLayout = columns.some((c) => c.width !== undefined);

  const head = (
    <TableHead>
      <TableRow>
        {columns.map((c) => (
          <TableCell
            key={c.id}
            align={c.align}
            sx={{
              width: c.width,
              fontWeight: 700,
              whiteSpace: 'nowrap',
              ...(c.always ? {} : { display: { xs: 'none', md: 'table-cell' } }),
            }}
          >
            {c.label}
          </TableCell>
        ))}
      </TableRow>
    </TableHead>
  );

  const renderRow = (row: T, style?: React.CSSProperties) => (
    <TableRow
      key={rowKey(row)}
      hover
      style={style}
      {...getRowProps?.(row)}
      sx={{ height: rowHeight }}
    >
      {columns.map((c) => (
        <TableCell
          key={c.id}
          align={c.align}
          sx={{
            whiteSpace: 'nowrap',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            maxWidth: c.width,
            ...(c.always ? {} : { display: { xs: 'none', md: 'table-cell' } }),
          }}
        >
          {c.render(row)}
        </TableCell>
      ))}
    </TableRow>
  );

  return (
    <TableContainer
      ref={parentRef}
      aria-label={ariaLabel}
      sx={{
        maxHeight,
        overflow: 'auto',
        borderRadius: 1,
        ...(disableBorder ? {} : { border: '1px solid', borderColor: 'divider' }),
      }}
    >
      <Table
        stickyHeader
        size={dense ? 'small' : 'medium'}
        sx={useFixedLayout ? { tableLayout: 'fixed', minWidth: 720 } : { minWidth: 720 }}
      >
        {head}
        <TableBody>
          {rows.length === 0 ? (
            <TableRow>
              <TableCell colSpan={columns.length} align="center" sx={{ py: 5 }}>
                <Typography variant="body2" color="text.secondary">
                  {emptyText}
                </Typography>
              </TableCell>
            </TableRow>
          ) : virtualize ? (
            rowVirtualizer.getVirtualItems().map((vi) => renderRow(rows[vi.index]!, { position: 'absolute', top: 0, left: 0, width: '100%', transform: `translateY(${vi.start}px)` }))
          ) : (
            rows.map((row) => renderRow(row))
          )}
        </TableBody>
      </Table>
      {virtualize ? (
        <div style={{ height: rowVirtualizer.getTotalSize(), width: 1 }} aria-hidden />
      ) : null}
    </TableContainer>
  );
}
