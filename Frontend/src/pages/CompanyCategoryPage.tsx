import { FormEvent, useCallback, useEffect, useState } from 'react';
import { api, ApiError, getToken, apiUrl } from '../api';
import Icon from '../components/Icon';
import { trackedFetch } from '../lib/loading';
import { confirmAction } from '../lib/confirm';
import { toast } from '../lib/toast';
import { Pager, SortArrow } from '../components/tableView';

interface Row {
  key: number;
  name: string;
  category: string;
  productId: number;
  product: string;
  bankId: number;
  bank: string;
  createdOn: string | null;
}
interface Option { id: number; name: string }
interface CategoryOption { name: string; count: number }

type SortKey = 'name' | 'category' | 'createdOn';

export default function CompanyCategoryPage() {
  const [rows, setRows] = useState<Row[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);
  const [sort, setSort] = useState<{ key: SortKey; dir: 'asc' | 'desc' }>({ key: 'name', dir: 'asc' });

  const [search, setSearch] = useState('');
  const [category, setCategory] = useState('');
  const [productId, setProductId] = useState('');
  const [bankId, setBankId] = useState('');

  const [categories, setCategories] = useState<CategoryOption[]>([]);
  const [products, setProducts] = useState<Option[]>([]);
  const [banks, setBanks] = useState<Option[]>([]);

  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [showUpload, setShowUpload] = useState(false);
  const [editRow, setEditRow] = useState<Row | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const params = new URLSearchParams();
      if (search) params.set('search', search);
      if (category) params.set('category', category);
      if (productId) params.set('productId', productId);
      if (bankId) params.set('bankId', bankId);
      params.set('sortBy', sort.key);
      params.set('sortDir', sort.dir);
      params.set('page', String(page));
      params.set('pageSize', String(pageSize));
      const data = await api.get<{ total: number; rows: Row[] }>('/api/company-categories?' + params.toString());
      setRows(data.rows);
      setTotal(data.total);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to load companies');
    } finally {
      setLoading(false);
    }
  }, [search, category, productId, bankId, sort, page, pageSize]);

  useEffect(() => { const t = setTimeout(load, search ? 350 : 0); return () => clearTimeout(t); }, [load]);

  // keep page valid when filters shrink the result set
  useEffect(() => { setPage(1); }, [search, category, productId, bankId, pageSize]);

  useEffect(() => {
    api.get<CategoryOption[]>('/api/company-categories/categories').then(setCategories).catch(() => setCategories([]));
    api.get<Option[]>('/api/company-categories/products').then(setProducts).catch(() => setProducts([]));
    api.get<Option[]>('/api/company-categories/banks').then(setBanks).catch(() => setBanks([]));
  }, []);

  const toggleSort = (key: SortKey) =>
    setSort((s) => (s.key === key ? { key, dir: s.dir === 'asc' ? 'desc' : 'asc' } : { key, dir: 'asc' }));

  const remove = async (r: Row) => {
    const ok = await confirmAction({
      title: `Delete "${r.name}"?`,
      text: `Category: ${r.category}. The selected row is removed permanently.`,
      confirmText: 'Delete',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await api.delete('/api/company-categories/' + r.key);
      toast.success(`Deleted "${r.name}".`);
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Delete failed');
    }
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Company Category Master</h1>
          <p className="page-sub">Company category mapping across products and banks</p>
        </div>
        <button className="btn primary" onClick={() => setShowUpload(true)}>
          <Icon name="upload" /> Upload Excel
        </button>
      </div>

      <div className="card">
        <div className="table-toolbar">
          <div className="search-box">
            <Icon name="search" />
            <input
              placeholder="Search by company name..."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>
          <select value={category} onChange={(e) => setCategory(e.target.value)} className="filter-select">
            <option value="">All Categories</option>
            {categories.map((c) => (
              <option key={c.name} value={c.name}>{c.name} ({c.count.toLocaleString()})</option>
            ))}
          </select>
          <select value={productId} onChange={(e) => setProductId(e.target.value)} className="filter-select">
            <option value="">All Products</option>
            {products.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
          </select>
          <select value={bankId} onChange={(e) => setBankId(e.target.value)} className="filter-select">
            <option value="">All Banks</option>
            {banks.map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
          </select>
        </div>

        {loading ? (
          <div className="loading">Loading…</div>
        ) : error ? (
          <div className="alert error">{error}</div>
        ) : (
          <div className="table-scroll">
            <table className="data-table">
              <thead>
                <tr>
                  <th>#</th>
                  <th className="sortable-th" onClick={() => toggleSort('name')}>
                    Company Name <SortArrow active={sort.key === 'name'} dir={sort.dir} />
                  </th>
                  <th className="sortable-th" onClick={() => toggleSort('category')}>
                    Category <SortArrow active={sort.key === 'category'} dir={sort.dir} />
                  </th>
                  <th>Product</th>
                  <th>Bank</th>
                  <th className="sortable-th" onClick={() => toggleSort('createdOn')}>
                    Added On <SortArrow active={sort.key === 'createdOn'} dir={sort.dir} />
                  </th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r, i) => (
                  <tr key={r.key}>
                    <td>{(page - 1) * pageSize + i + 1}</td>
                    <td>{r.name}</td>
                    <td><span className="badge blue">{r.category}</span></td>
                    <td>{r.product}</td>
                    <td>{r.bank}</td>
                    <td>{r.createdOn ? new Date(r.createdOn).toLocaleDateString() : '—'}</td>
                    <td>
                      <div className="row-actions">
                        <button className="icon-btn" title="Edit" onClick={() => setEditRow(r)}>
                          <Icon name="edit" size={16} />
                        </button>
                        <button className="icon-btn danger" title="Delete" onClick={() => remove(r)}>
                          <Icon name="trash" size={16} />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
                {rows.length === 0 && (
                  <tr><td colSpan={7} className="empty">No companies match the filters.</td></tr>
                )}
              </tbody>
            </table>
          </div>
        )}

        <Pager
          page={page}
          totalPages={Math.max(1, Math.ceil(total / pageSize))}
          totalRows={total}
          pageSize={pageSize}
          onPage={setPage}
          onPageSize={setPageSize}
        />
      </div>

      {showUpload && (
        <UploadModal
          products={products}
          banks={banks}
          onClose={() => setShowUpload(false)}
          onSaved={(msg) => { setShowUpload(false); setError(''); toast.success(msg); load(); }}
        />
      )}
      {editRow && (
        <EditModal
          row={editRow}
          products={products}
          banks={banks}
          onClose={() => setEditRow(null)}
          onSaved={() => { setEditRow(null); load(); }}
        />
      )}
    </div>
  );
}

function UploadModal({
  products, banks, onClose, onSaved,
}: {
  products: Option[]; banks: Option[]; onClose: () => void; onSaved: (msg: string) => void;
}) {
  const [file, setFile] = useState<File | null>(null);
  const [productId, setProductId] = useState('');
  const [bankId, setBankId] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!file || !productId || !bankId) return;
    setSaving(true);
    setError('');
    try {
      const fd = new FormData();
      fd.append('file', file);
      fd.append('productId', productId);
      fd.append('bankId', bankId);
      const res = await trackedFetch(apiUrl('/api/company-categories/upload'), {
        method: 'POST',
        headers: { Authorization: `Bearer ${getToken()}` },
        body: fd,
      });
      const data = await res.json().catch(() => null);
      if (!res.ok) throw new ApiError(data?.error || `Upload failed (${res.status})`, res.status);
      onSaved(
        `Uploaded successfully — ${data.inserted.toLocaleString()} new rows added` +
        (data.existingInDb ? `, ${data.existingInDb.toLocaleString()} already existed` : '') +
        (data.duplicatesSkipped ? `, ${data.duplicatesSkipped.toLocaleString()} skipped in file` : '') + '.'
      );
    } catch (e2) {
      setError(e2 instanceof ApiError ? e2.message : 'Upload failed');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <h3>Upload Company Categories</h3>
          <button className="icon-btn" onClick={onClose} aria-label="Close"><Icon name="close" size={16} /></button>
        </div>
        <form onSubmit={submit}>
          <div className="upload-note">
            Excel (.xlsx) with columns <b>Company Name</b> and <b>Final Category</b> — same format as the legacy CIPPortal upload. Rows are tied to the chosen Product + Bank; existing combinations are skipped.
          </div>
          <label className="field">
            <span className="field-label">EXCEL FILE (.XLSX)</span>
            <input
              type="file"
              accept=".xlsx"
              onChange={(e) => setFile(e.target.files?.[0] ?? null)}
              required
            />
          </label>
          <label className="field">
            <span className="field-label">PRODUCT *</span>
            <select value={productId} onChange={(e) => setProductId(e.target.value)} required>
              <option value="">Select product…</option>
              {products.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </label>
          <label className="field">
            <span className="field-label">BANK *</span>
            <select value={bankId} onChange={(e) => setBankId(e.target.value)} required>
              <option value="">Select bank…</option>
              {banks.map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
            </select>
          </label>
          {error && <div className="alert error">{error}</div>}
          <div className="modal-actions">
            <button type="button" className="btn" onClick={onClose}>Cancel</button>
            <button className="btn primary" disabled={saving || !file || !productId || !bankId}>
              {saving ? 'Uploading…' : 'Upload'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

function EditModal({
  row, products, banks, onClose, onSaved,
}: {
  row: Row; products: Option[]; banks: Option[]; onClose: () => void; onSaved: () => void;
}) {
  const [category, setCategory] = useState(row.category);
  const [productId, setProductId] = useState(String(row.productId));
  const [bankId, setBankId] = useState(String(row.bankId));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setError('');
    try {
      await api.put('/api/company-categories/' + row.key, {
        key: row.key,
        name: row.name,
        category,
        productId: Number(productId),
        bankId: Number(bankId),
      });
      onSaved();
    } catch (e2) {
      setError(e2 instanceof ApiError ? e2.message : 'Failed to save');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <h3>Edit — {row.name}</h3>
          <button className="icon-btn" onClick={onClose} aria-label="Close"><Icon name="close" size={16} /></button>
        </div>
        <form onSubmit={submit}>
          <label className="field">
            <span className="field-label">CATEGORY</span>
            <input value={category} onChange={(e) => setCategory(e.target.value)} required maxLength={100} />
          </label>
          <label className="field">
            <span className="field-label">PRODUCT</span>
            <select value={productId} onChange={(e) => setProductId(e.target.value)}>
              {products.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </label>
          <label className="field">
            <span className="field-label">BANK</span>
            <select value={bankId} onChange={(e) => setBankId(e.target.value)}>
              {banks.map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
            </select>
          </label>
          {error && <div className="alert error">{error}</div>}
          <div className="modal-actions">
            <button type="button" className="btn" onClick={onClose}>Cancel</button>
            <button className="btn primary" disabled={saving}>{saving ? 'Saving…' : 'Save Changes'}</button>
          </div>
        </form>
      </div>
    </div>
  );
}
