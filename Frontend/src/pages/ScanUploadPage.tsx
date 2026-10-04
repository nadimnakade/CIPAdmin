import { useCallback, useEffect, useRef, useState } from 'react';
import { api, apiUrl, ApiError, getToken } from '../api';
import Icon from '../components/Icon';
import { toast } from '../lib/toast';
import './ApplicationsPage.css';

interface ScanFile { name: string; size: string; modified: string; url: string }

/**
 * Scan File Upload. Replicates DataEntry_FileUpload_Controller, which simply wrote the
 * posted files under the application's folder; files are stored per application number
 * so they can be listed and downloaded again, which the legacy screen could not do.
 */
export default function ScanUploadPage() {
  const [appNo, setAppNo] = useState('');
  const [files, setFiles] = useState<FileList | null>(null);
  const [listing, setListing] = useState<ScanFile[]>([]);
  const [busy, setBusy] = useState(false);
  const [loadedFor, setLoadedFor] = useState('');
  const inputRef = useRef<HTMLInputElement>(null);

  const list = useCallback(async (n: string) => {
    if (!n.trim()) { setListing([]); return; }
    try {
      setListing(await api.get<ScanFile[]>(`/api/applications/scan/${encodeURIComponent(n.trim())}`));
      setLoadedFor(n.trim());
    } catch (e) {
      toast.error(e instanceof ApiError ? e.message : 'Could not list files.');
    }
  }, []);

  useEffect(() => { list(appNo); }, [appNo, list]);

  const upload = async () => {
    if (!appNo.trim()) { toast.error('Enter an application number.'); return; }
    if (!files || files.length === 0) { toast.error('Choose at least one file.'); return; }
    setBusy(true);
    try {
      const fd = new FormData();
      fd.append('appNo', appNo.trim());
      for (const f of Array.from(files)) fd.append('files', f);
      const res = await api.upload<{ ok: boolean; saved: string[] }>('/api/applications/scan', fd);
      toast.success(`${res.saved.length} file${res.saved.length === 1 ? '' : 's'} uploaded.`);
      setFiles(null);
      if (inputRef.current) inputRef.current.value = '';
      list(appNo);
    } catch (e) {
      toast.error(e instanceof ApiError ? e.message : 'Upload failed.');
    } finally {
      setBusy(false);
    }
  };

  const download = async (f: ScanFile) => {
    try {
      const res = await fetch(apiUrl(f.url), { headers: { Authorization: `Bearer ${getToken()}` } });
      if (!res.ok) throw new Error('Download failed');
      const blob = await res.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = f.name;
      a.click();
      URL.revokeObjectURL(url);
    } catch {
      toast.error('Could not download that file.');
    }
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Scan File Upload</h1>
          <p className="page-sub">
            Attach scanned documents (PAN, Aadhaar, salary slips, statements) to an application.
          </p>
        </div>
      </div>

      <div className="card">
        <div className="card-head"><h3>Upload</h3></div>
        <div className="card-body">
          <div className="filter-grid">
            <div className="field">
              <label className="field-label" htmlFor="scan-app">Application Number</label>
              <input id="scan-app" className="inp" value={appNo}
                onChange={(e) => setAppNo(e.target.value)} placeholder="e.g. 11270790" />
            </div>
            <div className="field">
              <label className="field-label" htmlFor="scan-files">Files</label>
              <input id="scan-files" className="inp" type="file" multiple ref={inputRef}
                onChange={(e) => setFiles(e.target.files)} />
              <span className="field-hint">
                {files && files.length > 0
                  ? Array.from(files).map((f) => f.name).join(', ')
                  : 'Any file type; stored under the application folder.'}
              </span>
            </div>
            <div className="filter-actions">
              <button className="btn primary" onClick={upload} disabled={busy}>
                <Icon name="upload" /> {busy ? 'Uploading…' : 'Upload'}
              </button>
            </div>
          </div>
        </div>
      </div>

      <div className="card">
        <div className="card-head">
          <h3>Files for {loadedFor || '—'}</h3>
          <span className="card-head-note">{listing.length} file{listing.length === 1 ? '' : 's'}</span>
        </div>
        <div className="table-wrap">
          <table className="data-table">
            <thead>
              <tr><th>File</th><th className="num">Size</th><th>Modified</th><th className="num">Actions</th></tr>
            </thead>
            <tbody>
              {listing.map((f) => (
                <tr key={f.name}>
                  <td>{f.name}</td>
                  <td className="num">{f.size}</td>
                  <td>{f.modified}</td>
                  <td className="num">
                    <button className="icon-btn" title="Download" aria-label={`Download ${f.name}`}
                      onClick={() => download(f)}><Icon name="download" /></button>
                  </td>
                </tr>
              ))}
              {listing.length === 0 && (
                <tr><td colSpan={4} className="empty-cell">
                  {loadedFor ? 'No files stored for this application yet.' : 'Enter an application number to see its files.'}
                </td></tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}