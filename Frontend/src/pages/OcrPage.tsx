import { useCallback, useEffect, useRef, useState } from 'react';
import { api, ApiError } from '../api';
import Icon from '../components/Icon';

interface OcrExtractResponse {
  jobId: number;
  fileName: string;
  rows: string[][];
  rowCount: number;
  colCount: number;
  wordCount: number;
  previewUrl: string;
}

interface OcrJob {
  jobId: number;
  originalFileName: string;
  fileSize: number;
  contentType: string | null;
  rowCount: number;
  colCount: number;
  createdDate: string;
}

const IMAGE_OK = /\.(jpe?g|png|bmp|gif|webp|tiff?)$/i;

export default function OcrPage() {
  const [file, setFile] = useState<File | null>(null);
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [extracting, setExtracting] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [result, setResult] = useState<OcrExtractResponse | null>(null);
  const [grid, setGrid] = useState<string[][]>([]);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [drag, setDrag] = useState(false);

  const [history, setHistory] = useState<OcrJob[]>([]);
  const [loadingHistory, setLoadingHistory] = useState(true);
  const inputRef = useRef<HTMLInputElement>(null);

  const loadHistory = useCallback(async () => {
    setLoadingHistory(true);
    try {
      setHistory(await api.get<OcrJob[]>('/api/ocr/jobs'));
      setError('');
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to load history');
    } finally {
      setLoadingHistory(false);
    }
  }, []);

  useEffect(() => {
    loadHistory();
  }, [loadHistory]);

  const pickFile = (f: File | null | undefined) => {
    if (!f) return;
    if (!f.type.startsWith('image/') && !IMAGE_OK.test(f.name)) {
      setError('Please choose an image file (JPG, PNG, BMP, GIF, WEBP or TIFF).');
      return;
    }
    if (f.size > 10 * 1024 * 1024) {
      setError('File exceeds the 10 MB limit.');
      return;
    }
    setError('');
    setNotice('');
    setFile(f);
    if (previewUrl) URL.revokeObjectURL(previewUrl);
    setPreviewUrl(URL.createObjectURL(f));
  };

  const extract = async () => {
    if (!file) return;
    setExtracting(true);
    setError('');
    setNotice('');
    try {
      const form = new FormData();
      form.append('file', file);
      const data = await api.upload<OcrExtractResponse>('/api/ocr/extract', form);
      setResult(data);
      setGrid(data.rows.map((r) => r.map((v) => v ?? '')));
      setDirty(false);
      setNotice(
        data.wordCount > 0
          ? `Extracted ${data.rowCount} rows × ${data.colCount} columns (${data.wordCount} words). Review the grid, edit any cell, then export.`
          : 'Converted — review the grid, edit any cell, then export.'
      );
      loadHistory();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Conversion failed');
    } finally {
      setExtracting(false);
    }
  };

  const cols = grid[0]?.length ?? 0;

  const setCell = (r: number, c: number, v: string) => {
    setGrid((g) => g.map((row, ri) => (ri === r ? row.map((cell, ci) => (ci === c ? v : cell)) : row)));
    setDirty(true);
  };
  const addRow = () => {
    setGrid((g) => [...g, new Array(Math.max(1, g[0]?.length ?? 1)).fill('')]);
    setDirty(true);
  };
  const deleteRow = (r: number) => {
    setGrid((g) => (g.length <= 1 ? g : g.filter((_, i) => i !== r)));
    setDirty(true);
  };
  const addCol = () => {
    setGrid((g) => g.map((row) => [...row, '']));
    setDirty(true);
  };
  const deleteCol = (c: number) => {
    setGrid((g) => ((g[0]?.length ?? 0) <= 1 ? g : g.map((row) => row.filter((_, i) => i !== c))));
    setDirty(true);
  };

  const saveEdits = async () => {
    if (!result) return;
    setSaving(true);
    setError('');
    try {
      await api.put(`/api/ocr/jobs/${result.jobId}/grid`, { grid });
      setDirty(false);
      setNotice('Edits saved to the conversion history.');
      loadHistory();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to save');
    } finally {
      setSaving(false);
    }
  };

  const exportExcel = async () => {
    if (grid.length === 0) return;
    setExporting(true);
    setError('');
    try {
      const blob = await api.download('/api/ocr/export', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ grid }),
      });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `OCR_Extract_${new Date().toISOString().slice(0, 10)}.xlsx`;
      a.click();
      URL.revokeObjectURL(url);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Export failed');
    } finally {
      setExporting(false);
    }
  };

  const openJob = async (job: OcrJob) => {
    setError('');
    setNotice('');
    try {
      const data = await api.get<{ jobId: number; fileName: string; rows: string[][] }>(
        `/api/ocr/jobs/${job.jobId}/grid`
      );
      const blob = await api.download(`/api/ocr/jobs/${job.jobId}/image`);
      if (previewUrl) URL.revokeObjectURL(previewUrl);
      setPreviewUrl(URL.createObjectURL(blob));
      setResult({
        jobId: data.jobId,
        fileName: data.fileName,
        rows: data.rows,
        rowCount: data.rows.length,
        colCount: data.rows[0]?.length ?? 0,
        wordCount: -1,
        previewUrl: '',
      });
      setGrid(data.rows.map((r) => (r ?? []).map((v) => v ?? '')));
      setDirty(false);
      setNotice(`Opened “${data.fileName}” from history.`);
      window.scrollTo({ top: 0, behavior: 'smooth' });
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Failed to open conversion');
    }
  };

  const deleteJob = async (job: OcrJob) => {
    if (!confirm(`Delete the conversion of “${job.originalFileName}” from history?`)) return;
    try {
      await api.delete(`/api/ocr/jobs/${job.jobId}`);
      if (result?.jobId === job.jobId) {
        setResult(null);
        setGrid([]);
      }
      setNotice('Conversion deleted.');
      loadHistory();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Delete failed');
    }
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>OCR to Excel</h1>
          <p className="page-sub">Upload a photo or screenshot of a spreadsheet — convert it to editable data and export to Excel</p>
        </div>
      </div>

      {notice && <div className="alert success">{notice}</div>}
      {error && <div className="alert error">{error}</div>}

      <div className="card">
        <div
          className={'dropzone' + (drag ? ' drag' : '')}
          onDragOver={(e) => {
            e.preventDefault();
            setDrag(true);
          }}
          onDragLeave={() => setDrag(false)}
          onDrop={(e) => {
            e.preventDefault();
            setDrag(false);
            pickFile(e.dataTransfer.files?.[0]);
          }}
          onClick={() => inputRef.current?.click()}
          role="button"
          tabIndex={0}
        >
          <Icon name="upload" size={26} />
          <div>
            <strong>{file ? file.name : 'Drop an image here or click to browse'}</strong>
            <p className="dropzone-sub">JPG, PNG, BMP, GIF, WEBP or TIFF · up to 10 MB · sharper, straight-on photos convert best</p>
          </div>
          <input
            ref={inputRef}
            type="file"
            accept="image/*"
            hidden
            onChange={(e) => pickFile(e.target.files?.[0])}
          />
        </div>
        <div className="ocr-actions">
          <button className="btn primary" disabled={!file || extracting} onClick={extract}>
            <Icon name="search" size={16} /> {extracting ? 'Converting…' : 'Convert to Excel Data'}
          </button>
          {file && (
            <span className="ocr-file-meta">
              {(file.size / 1024).toFixed(0)} KB
            </span>
          )}
        </div>
      </div>

      {result && (
        <div className="card">
          <div className="table-toolbar">
            <div className="ocr-title">
              <Icon name="report" size={18} />
              <strong>{result.fileName}</strong>
              {result.wordCount >= 0 && (
                <span className="badge gray">
                  {grid.length} rows × {cols} cols
                </span>
              )}
              {dirty && <span className="badge orange">unsaved edits</span>}
            </div>
            <div className="row-actions">
              <button className="btn small" onClick={addRow}><Icon name="plus" size={14} /> Row</button>
              <button className="btn small" onClick={addCol}><Icon name="plus" size={14} /> Column</button>
              <button className="btn small" onClick={saveEdits} disabled={!dirty || saving}>
                {saving ? 'Saving…' : 'Save Edits'}
              </button>
              <button className="btn small primary" onClick={exportExcel} disabled={exporting || grid.length === 0}>
                <Icon name="download" size={14} /> {exporting ? 'Exporting…' : 'Export to Excel'}
              </button>
            </div>
          </div>

          <div className="ocr-split">
            <div className="ocr-preview">
              {previewUrl && <img src={previewUrl} alt="Uploaded sheet" />}
            </div>
            <div className="table-scroll ocr-grid-wrap">
              <table className="data-table ocr-grid-table">
                <tbody>
                  {grid.map((row, r) => (
                    <tr key={r}>
                      <td className="ocr-row-num">{r + 1}</td>
                      {row.map((cell, c) => (
                        <td key={c}>
                          <input
                            className="ocr-grid-input"
                            value={cell}
                            onChange={(e) => setCell(r, c, e.target.value)}
                            aria-label={`Row ${r + 1} column ${c + 1}`}
                          />
                        </td>
                      ))}
                      <td className="ocr-row-actions">
                        <button className="icon-btn danger" title="Delete row" onClick={() => deleteRow(r)}>
                          <Icon name="close" size={13} />
                        </button>
                      </td>
                    </tr>
                  ))}
                  {grid.length > 0 && (
                    <tr>
                      <td className="ocr-row-num"></td>
                      {grid[0].map((_, c) => (
                        <td key={c} className="ocr-col-actions">
                          <button className="icon-btn danger" title="Delete column" onClick={() => deleteCol(c)}>
                            <Icon name="close" size={13} />
                          </button>
                        </td>
                      ))}
                      <td></td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </div>
          <p className="ocr-hint">
            Click any cell to fix an OCR mistake before exporting. The first row becomes the Excel header.
          </p>
        </div>
      )}

      <div className="card">
        <div className="table-toolbar">
          <h3 className="section-title">Conversion History</h3>
        </div>
        {loadingHistory ? (
          <div className="loading">Loading…</div>
        ) : history.length === 0 ? (
          <div className="empty">No conversions yet — upload an image above to get started.</div>
        ) : (
          <div className="table-scroll">
            <table className="data-table">
              <thead>
                <tr>
                  <th>#</th>
                  <th>File</th>
                  <th>Size</th>
                  <th>Grid</th>
                  <th>Converted</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                {history.map((j, i) => (
                  <tr key={j.jobId}>
                    <td>{i + 1}</td>
                    <td>{j.originalFileName}</td>
                    <td>{(j.fileSize / 1024).toFixed(0)} KB</td>
                    <td>{j.rowCount} × {j.colCount}</td>
                    <td>{new Date(j.createdDate).toLocaleString()}</td>
                    <td>
                      <div className="row-actions">
                        <button className="btn small" onClick={() => openJob(j)}>
                          <Icon name="eye" size={14} /> Open
                        </button>
                        <button className="icon-btn danger" title="Delete conversion" onClick={() => deleteJob(j)}>
                          <Icon name="trash" size={15} />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}
