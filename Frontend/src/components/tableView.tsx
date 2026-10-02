import { useEffect, useMemo, useState } from 'react';
import Icon from './Icon';

export interface SortState<K extends string> {
  key: K;
  dir: 'asc' | 'desc';
}

/**
 * Client-side sorting + pagination over any row array.
 * getCellValue must return a number for numeric columns so sorting is numeric.
 */
export function useTableView<T, K extends string>(
  rows: T[],
  defaultSortKey: K,
  getCellValue: (row: T, key: K) => string | number | null | undefined
) {
  const [sort, setSort] = useState<SortState<K>>({ key: defaultSortKey, dir: 'desc' });
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = useState(50);

  // Keep page in range when filters shrink the dataset
  const totalPages = Math.max(1, Math.ceil(rows.length / pageSize));
  useEffect(() => {
    if (page > totalPages - 1) setPage(totalPages - 1);
  }, [totalPages, page]);

  const toggleSort = (key: K) => {
    setSort((s) =>
      s.key === key
        ? { key, dir: s.dir === 'asc' ? 'desc' : 'asc' }
        : { key, dir: 'desc' }
    );
  };

  const sorted = useMemo(() => {
    const dir = sort.dir === 'asc' ? 1 : -1;
    return [...rows].sort((a, b) => {
      const va = getCellValue(a, sort.key);
      const vb = getCellValue(b, sort.key);
      if (typeof va === 'number' && typeof vb === 'number') return (va - vb) * dir;
      return String(va ?? '').localeCompare(String(vb ?? '')) * dir;
    });
  }, [rows, sort, getCellValue]);

  const paged = useMemo(
    () => sorted.slice(page * pageSize, page * pageSize + pageSize),
    [sorted, page, pageSize]
  );

  return {
    sort,
    toggleSort,
    page,
    setPage,
    pageSize,
    setPageSize: (n: number) => {
      setPageSize(n);
      setPage(0);
    },
    totalPages,
    totalRows: rows.length,
    pageRows: paged,
    sorted,
  };
}

/** Reusable pager bar: page-size selector + first/prev/next/last. */
export function Pager({
  page,
  totalPages,
  totalRows,
  pageSize,
  onPage,
  onPageSize,
  sizes = [25, 50, 100],
}: {
  page: number;
  totalPages: number;
  totalRows: number;
  pageSize: number;
  onPage: (p: number) => void;
  onPageSize: (n: number) => void;
  sizes?: number[];
}) {
  const from = totalRows === 0 ? 0 : page * pageSize + 1;
  const to = Math.min(totalRows, page * pageSize + pageSize);
  return (
    <div className="pager-bar">
      <span className="pager-count">
        {from.toLocaleString('en-IN')}–{to.toLocaleString('en-IN')} of{' '}
        {totalRows.toLocaleString('en-IN')}
      </span>
      <select value={pageSize} onChange={(e) => onPageSize(Number(e.target.value))} className="pager-size">
        {sizes.map((s) => (
          <option key={s} value={s}>
            {s === 100000 ? 'All' : `${s} / page`}
          </option>
        ))}
      </select>
      <div className="pager-btns">
        <button className="btn small" disabled={page === 0} onClick={() => onPage(0)} title="First" aria-label="First page"><Icon name="chevronsLeft" size={14} /></button>
        <button className="btn small" disabled={page === 0} onClick={() => onPage(page - 1)} title="Previous" aria-label="Previous page"><Icon name="chevronLeft" size={14} /></button>
        <span className="pager-info">Page {page + 1} / {totalPages}</span>
        <button className="btn small" disabled={page >= totalPages - 1} onClick={() => onPage(page + 1)} title="Next" aria-label="Next page"><Icon name="chevronRight" size={14} /></button>
        <button className="btn small" disabled={page >= totalPages - 1} onClick={() => onPage(totalPages - 1)} title="Last" aria-label="Last page"><Icon name="chevronsRight" size={14} /></button>
      </div>
    </div>
  );
}

/** Sort indicator for table headers. */
export function SortArrow({ active, dir }: { active: boolean; dir: 'asc' | 'desc' }) {
  return <span className="sort-arrow">{active ? (dir === 'asc' ? ' ↑' : ' ↓') : ''}</span>;
}
